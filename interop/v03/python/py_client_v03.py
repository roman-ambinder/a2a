"""Drives any A2A v0.3 agent that implements the interop scenarios, using the official a2a-sdk 0.3.26 client.

Usage: python py_client_v03.py <agentBaseUrl> <JSONRPC|HTTP+JSON|GRPC>
Exit code: number of failed checks.
No ListTasks: it is not in v0.3 JSON-RPC or gRPC, and the 0.3 Python SDK does not implement it.
"""

import asyncio
import json
import sys
import traceback
import uuid

import grpc
import httpx

from a2a.client import A2ACardResolver, ClientConfig, ClientFactory
from a2a.client.errors import A2AClientHTTPError, A2AClientJSONRPCError
from a2a.types import (
    GetTaskPushNotificationConfigParams,
    Message,
    MessageSendConfiguration,
    Part,
    PushNotificationConfig,
    Role,
    TaskIdParams,
    TaskPushNotificationConfig,
    TaskQueryParams,
    TaskState,
    TextPart,
)
from a2a.utils.message import get_message_text

TERMINAL = {TaskState.completed, TaskState.failed, TaskState.canceled, TaskState.rejected}
TASK_NOT_FOUND = -32001


def user_msg(text: str, task_id: str | None = None, context_id: str | None = None) -> Message:
    return Message(role=Role.user, parts=[Part(root=TextPart(text=text))], message_id=str(uuid.uuid4()),
                   task_id=task_id, context_id=context_id)


def expect(cond: bool, what: str) -> None:
    if not cond:
        raise AssertionError('expected ' + what)


async def collect(aiter):
    return [e async for e in aiter]


async def webhook_receiver():
    """A minimal HTTP server for push deliveries: queues (headers, JSON body) and answers 200."""
    received: asyncio.Queue = asyncio.Queue()

    async def handle(reader, writer):
        try:
            head = (await reader.readuntil(b'\r\n\r\n')).decode('latin-1').split('\r\n')
            headers = {k.strip().lower(): v.strip() for k, v in (line.split(':', 1) for line in head[1:] if ':' in line)}
            body = await reader.readexactly(int(headers.get('content-length', '0')))
            await received.put((headers, json.loads(body) if body else None))
            writer.write(b'HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n')
            await writer.drain()
        finally:
            writer.close()

    server = await asyncio.start_server(handle, '127.0.0.1', 0)
    return server, server.sockets[0].getsockname()[1], received


async def main(url: str, binding: str) -> int:
    failures = 0
    http = httpx.AsyncClient(timeout=30)

    def channel(url: str):
        return grpc.aio.insecure_channel(url.split('://', 1)[-1].rstrip('/'))

    def config(stream: bool) -> ClientConfig:
        return ClientConfig(streaming=stream, supported_transports=[binding], httpx_client=http, grpc_channel_factory=channel)

    card = await A2ACardResolver(http, url).get_agent_card()
    blocking = ClientFactory(config(False)).create(card)
    streaming = ClientFactory(config(True)).create(card)
    print(f'# Python v0.3 client -> {card.name} via {binding}')

    async def check(name, coro):
        nonlocal failures
        try:
            await asyncio.wait_for(coro(), 20)
            print(f'PASS  {name}')
        except Exception as ex:  # noqa: BLE001
            failures += 1
            print(f'FAIL  {name}: {type(ex).__name__}: {ex}')
            traceback.print_exc(limit=3, file=sys.stdout)

    state = {}

    def is_message(e) -> bool:
        return isinstance(e, Message)

    async def direct_reply():
        events = await collect(blocking.send_message(user_msg('hello')))
        expect(len(events) == 1 and is_message(events[0]), 'one message')
        expect(get_message_text(events[0]) == 'hi there', "text 'hi there'")
        expect(events[0].role == Role.agent, 'agent role')

    async def blocking_task():
        events = await collect(blocking.send_message(user_msg('task')))
        task = events[0][0]
        expect(task.status.state == TaskState.completed, f'completed, got {task.status.state}')
        expect(len(task.artifacts or []) == 1, f'one artifact, got {len(task.artifacts or [])}')
        texts = [p.root.text for p in task.artifacts[0].parts]
        expect(texts == ['part1', 'part2'], f'parts [part1, part2], got {texts}')
        state['completed'] = task

    async def streaming_task():
        events = await collect(streaming.send_message(user_msg('task')))
        # The 0.3 client folds each stream event into a (task, update) pair: task, working, 2 artifact chunks, completed.
        expect(len(events) >= 4, f'>= 4 events, got {len(events)}')
        expect(events[0][1] is None and events[0][0].id, 'first event to be the Task')
        expect(any(getattr(u, 'append', None) for _, u in events), 'an append chunk')
        last_task = events[-1][0]
        expect(last_task.status.state == TaskState.completed, f'last completed, got {last_task.status.state}')
        expect([p.root.text for p in last_task.artifacts[0].parts] == ['part1', 'part2'], 'folded artifact parts part1, part2')

    async def streaming_message():
        events = await collect(streaming.send_message(user_msg('hello')))
        expect(len(events) == 1 and is_message(events[0]) and get_message_text(events[0]) == 'hi there', 'exactly one message event')

    async def multi_turn():
        first = (await collect(blocking.send_message(user_msg('input'))))[0][0]
        expect(first.status.state == TaskState.input_required, f'input-required, got {first.status.state}')
        second = (await collect(blocking.send_message(user_msg('more', first.id, first.context_id))))[0][0]
        expect(second.id == first.id, 'same task id')
        expect(second.status.state == TaskState.completed, f'completed, got {second.status.state}')
        got = get_message_text(second.status.message)
        expect(got == 'got: more', f"'got: more', got '{got}'")

    async def get_task():
        full = await blocking.get_task(TaskQueryParams(id=state['completed'].id))
        expect(len(full.history or []) > 1, f'more than 1 history entry without a limit, got {len(full.history or [])}')
        t = await blocking.get_task(TaskQueryParams(id=state['completed'].id, history_length=1))
        expect(t.id == state['completed'].id, 'same id')
        expect(len(t.history or []) == 1, f'exactly 1 history entry, got {len(t.history or [])}')

    async def unknown_task():
        try:
            await blocking.get_task(TaskQueryParams(id=f'does-not-exist-{uuid.uuid4()}'))
        except A2AClientJSONRPCError as ex:
            expect(ex.error.code == TASK_NOT_FOUND, f'code {TASK_NOT_FOUND}, got {ex.error.code}')
            return
        except A2AClientHTTPError as ex:
            # v0.3 REST maps TaskNotFound to HTTP 404.
            expect(ex.status_code == 404, f'HTTP 404, got {ex.status_code}')
            return
        except grpc.aio.AioRpcError as ex:
            # v0.3 gRPC, as the 0.3 Python server answers: NOT_FOUND and a message that starts with the error name.
            expect(ex.code() == grpc.StatusCode.NOT_FOUND, f'NOT_FOUND, got {ex.code()}')
            expect(ex.details().startswith('TaskNotFoundError'), f'TaskNotFoundError..., got {ex.details()!r}')
            return
        raise AssertionError('expected a TaskNotFound error')

    async def subscribe_cancel():
        events = await collect(blocking.send_message(user_msg('slow'), configuration=MessageSendConfiguration(blocking=False)))
        task = events[0][0]
        expect(task.status.state not in TERMINAL, f'non-terminal, got {task.status.state}')

        seen = []

        async def reader():
            async for t, _ in streaming.resubscribe(TaskIdParams(id=task.id)):
                seen.append(t.status.state)

        # v0.3 does not promise a snapshot first (the 0.3 Python server sends none), so give the subscription time to attach.
        sub = asyncio.create_task(reader())
        await asyncio.sleep(1)
        expect(not sub.done(), 'the subscription to stay open while the task runs')
        canceled = await blocking.cancel_task(TaskIdParams(id=task.id))
        expect(canceled.status.state == TaskState.canceled, f'canceled, got {canceled.status.state}')
        await asyncio.wait_for(sub, 10)
        expect(TaskState.canceled in seen, f'subscription to see canceled, saw {[str(s) for s in seen]}')

    async def extended_card():
        # An agent that offers an extended card must serve it. The .NET agent runs anonymous and offers none.
        offered = bool(card.supports_authenticated_extended_card)
        c = await blocking.get_card()
        if offered:
            expect(c.name.endswith('(extended)'), f'extended card, got {c.name!r}')
        else:
            expect(c.name == card.name, f'the public card, got {c.name!r}')

    async def push_notifications():
        if not card.capabilities.push_notifications:
            raise AssertionError('the agent to offer push notifications')
        server, port, received = await webhook_receiver()
        try:
            first = (await collect(blocking.send_message(user_msg('input'))))[0][0]
            created = await blocking.set_task_callback(TaskPushNotificationConfig(
                task_id=first.id,
                push_notification_config=PushNotificationConfig(url=f'http://127.0.0.1:{port}/hook', token='tok-v03')))
            expect(created.push_notification_config.url.endswith('/hook'), 'the webhook url back')
            got = await blocking.get_task_callback(GetTaskPushNotificationConfigParams(
                id=first.id, push_notification_config_id=created.push_notification_config.id))
            expect(got.push_notification_config.url == created.push_notification_config.url, 'the same config')
            await collect(blocking.send_message(user_msg('more', first.id, first.context_id)))
            while True:
                headers, body = await asyncio.wait_for(received.get(), 10)
                if (body or {}).get('status', {}).get('state') == 'completed':
                    break
            # The v0.3 body: the task (what the 0.3 Python server sends too), not a v1.0 StreamResponse.
            expect(body.get('kind') == 'task' and body.get('id') == first.id, f'a v0.3 Task body, got {str(body)[:200]}')
            expect(headers.get('x-a2a-notification-token') == 'tok-v03', f"the token header, got {headers.get('x-a2a-notification-token')!r}")
        finally:
            server.close()

    await check('direct message reply', direct_reply)
    await check('blocking send -> completed task with appended artifact', blocking_task)
    await check('streaming send -> task first, ordered events, terminal last', streaming_task)
    await check('streaming direct message', streaming_message)
    await check('multi-turn input-required', multi_turn)
    await check('get task with historyLength', get_task)
    await check('get unknown task -> TaskNotFound', unknown_task)
    await check('return immediately, resubscribe, cancel', subscribe_cancel)
    await check('extended card (if offered)', extended_card)
    await check('push notification: config, then a v0.3 Task body at the webhook', push_notifications)

    print('# all checks passed' if failures == 0 else f'# {failures} check(s) failed')
    await http.aclose()
    return failures


if __name__ == '__main__':
    sys.exit(asyncio.run(main(sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:5500', sys.argv[2] if len(sys.argv) > 2 else 'JSONRPC')))
