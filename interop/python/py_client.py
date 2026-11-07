"""Drives any A2A v1.0 agent that implements the interop scenarios, using the official a2a-sdk client.

Usage: python py_client.py <agentBaseUrl> <JSONRPC|HTTP+JSON>
Exit code: number of failed checks.
"""

import asyncio
import sys
import traceback
import uuid

import grpc
import httpx

from a2a.client import ClientConfig, create_client
from a2a.helpers.proto_helpers import get_message_text, new_text_message
from a2a.types import (
    CancelTaskRequest,
    GetTaskRequest,
    ListTasksRequest,
    Role,
    SendMessageConfiguration,
    SendMessageRequest,
    SubscribeToTaskRequest,
    TaskState,
)
from a2a.utils.errors import TaskNotFoundError

TERMINAL = {TaskState.TASK_STATE_COMPLETED, TaskState.TASK_STATE_FAILED, TaskState.TASK_STATE_CANCELED, TaskState.TASK_STATE_REJECTED}


def user_msg(text: str, task_id: str = '', context_id: str = ''):
    m = new_text_message(text, role=Role.ROLE_USER)
    if task_id:
        m.task_id = task_id
    if context_id:
        m.context_id = context_id
    return m


def expect(cond: bool, what: str) -> None:
    if not cond:
        raise AssertionError('expected ' + what)


async def collect(aiter):
    return [e async for e in aiter]


async def main(url: str, binding: str) -> int:
    failures = 0
    http = httpx.AsyncClient(timeout=30)
    def channel(target: str):
        return grpc.aio.insecure_channel(target.split('://', 1)[-1].rstrip('/'))

    def config(stream: bool) -> ClientConfig:
        return ClientConfig(streaming=stream, supported_protocol_bindings=[binding], httpx_client=http, grpc_channel_factory=channel)

    blocking = await create_client(url, config(False))
    streaming = await create_client(url, config(True))
    print(f'# Python client -> {blocking._card.name} via {binding}')

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

    async def direct_reply():
        events = await collect(blocking.send_message(SendMessageRequest(message=user_msg('hello'))))
        expect(len(events) == 1 and events[0].HasField('message'), 'one message')
        expect(get_message_text(events[0].message) == 'hi there', "text 'hi there'")
        expect(events[0].message.role == Role.ROLE_AGENT, 'agent role')

    async def blocking_task():
        events = await collect(blocking.send_message(SendMessageRequest(message=user_msg('task'))))
        task = events[0].task
        expect(task.status.state == TaskState.TASK_STATE_COMPLETED, f'COMPLETED, got {TaskState.Name(task.status.state)}')
        expect(len(task.artifacts) == 1, f'one artifact, got {len(task.artifacts)}')
        texts = [p.text for p in task.artifacts[0].parts]
        expect(texts == ['part1', 'part2'], f'parts [part1, part2], got {texts}')
        state['completed'] = task

    async def streaming_task():
        events = await collect(streaming.send_message(SendMessageRequest(message=user_msg('task'))))
        expect(len(events) >= 4, f'>= 4 events, got {len(events)}')
        expect(events[0].HasField('task'), 'first event to be a Task')
        expect(any(e.HasField('artifact_update') and e.artifact_update.append for e in events), 'an append chunk')
        last = [e for e in events if e.HasField('status_update') or e.HasField('task')][-1]
        st = last.status_update.status.state if last.HasField('status_update') else last.task.status.state
        expect(st == TaskState.TASK_STATE_COMPLETED, f'last COMPLETED, got {TaskState.Name(st)}')

    async def streaming_message():
        events = await collect(streaming.send_message(SendMessageRequest(message=user_msg('hello'))))
        expect(len(events) == 1 and get_message_text(events[0].message) == 'hi there', 'exactly one message event')

    async def multi_turn():
        first = (await collect(blocking.send_message(SendMessageRequest(message=user_msg('input')))))[0].task
        expect(first.status.state == TaskState.TASK_STATE_INPUT_REQUIRED, f'INPUT_REQUIRED, got {TaskState.Name(first.status.state)}')
        second = (await collect(blocking.send_message(SendMessageRequest(message=user_msg('more', first.id, first.context_id)))))[0].task
        expect(second.id == first.id, 'same task id')
        expect(second.status.state == TaskState.TASK_STATE_COMPLETED, f'COMPLETED, got {TaskState.Name(second.status.state)}')
        expect(get_message_text(second.status.message) == 'got: more', f"'got: more', got '{get_message_text(second.status.message)}'")

    async def get_task():
        t = await blocking.get_task(GetTaskRequest(id=state['completed'].id, history_length=1))
        expect(t.id == state['completed'].id, 'same id')
        expect(len(t.history) <= 1, f'<= 1 history entries, got {len(t.history)}')

    async def list_tasks():
        page = await blocking.list_tasks(ListTasksRequest(page_size=50))
        expect(len(page.tasks) > 0, 'some tasks')
        expect(any(t.id == state['completed'].id for t in page.tasks), 'the completed task in the list')

    async def unknown_task():
        try:
            await blocking.get_task(GetTaskRequest(id=f'does-not-exist-{uuid.uuid4()}'))
        except TaskNotFoundError:
            return
        raise AssertionError('expected TaskNotFoundError')

    async def subscribe_cancel():
        req = SendMessageRequest(message=user_msg('slow'), configuration=SendMessageConfiguration(return_immediately=True))
        task = (await collect(blocking.send_message(req)))[0].task
        expect(task.status.state not in TERMINAL, f'non-terminal, got {TaskState.Name(task.status.state)}')

        seen = []
        first = asyncio.get_running_loop().create_future()

        async def reader():
            async for e in streaming.subscribe(SubscribeToTaskRequest(id=task.id)):
                if not first.done():
                    first.set_result(e)
                if e.HasField('status_update'):
                    seen.append(e.status_update.status.state)

        sub = asyncio.create_task(reader())
        ev = await asyncio.wait_for(first, 10)
        expect(ev.HasField('task') and ev.task.id == task.id, 'subscription to start with the task snapshot')
        canceled = await blocking.cancel_task(CancelTaskRequest(id=task.id))
        expect(canceled.status.state == TaskState.TASK_STATE_CANCELED, f'CANCELED, got {TaskState.Name(canceled.status.state)}')
        await asyncio.wait_for(sub, 10)
        expect(TaskState.TASK_STATE_CANCELED in seen, f'subscription to see CANCELED, saw {[TaskState.Name(s) for s in seen]}')

    await check('direct message reply', direct_reply)
    await check('blocking send -> completed task with appended artifact', blocking_task)
    await check('streaming send -> task first, ordered events, terminal last', streaming_task)
    await check('streaming direct message', streaming_message)
    await check('multi-turn input-required', multi_turn)
    await check('get task with historyLength', get_task)
    await check('list tasks', list_tasks)
    await check('get unknown task -> TaskNotFound', unknown_task)
    await check('return immediately, subscribe, cancel', subscribe_cancel)

    print('# all checks passed' if failures == 0 else f'# {failures} check(s) failed')
    await http.aclose()
    return failures


if __name__ == '__main__':
    sys.exit(asyncio.run(main(sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:5300', sys.argv[2] if len(sys.argv) > 2 else 'JSONRPC')))
