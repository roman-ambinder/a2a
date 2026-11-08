"""Official a2a-sdk 0.3.26 (protocol v0.3) agent with the same scenarios as the .NET interop agent.

Usage: python py_server_v03.py <port>
Serves JSON-RPC at POST / and HTTP+JSON under /v1, card at /.well-known/agent-card.json.
"""

import asyncio
import os
import sys

import uvicorn

from a2a.server.agent_execution import AgentExecutor, RequestContext
from a2a.server.apps import A2AStarletteApplication
from a2a.server.apps.rest import A2ARESTFastAPIApplication
from a2a.server.events import EventQueue
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.tasks import InMemoryTaskStore, TaskUpdater
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentInterface,
    AgentSkill,
    Part,
    TextPart,
)
from a2a.utils import new_agent_text_message, new_task
from a2a.utils.message import get_message_text


def text(value: str) -> Part:
    return Part(root=TextPart(text=value))


class ScenarioExecutor(AgentExecutor):
    async def execute(self, context: RequestContext, event_queue: EventQueue) -> None:
        msg = context.message
        value = get_message_text(msg) if msg else ''
        task = context.current_task

        if value.startswith('hello') and task is None:
            await event_queue.enqueue_event(new_agent_text_message('hi there', context.context_id))
            return

        if task is None:
            task = new_task(msg)
            await event_queue.enqueue_event(task)
        updater = TaskUpdater(event_queue, task.id, task.context_id)

        if task.status.state.value == 'input-required':
            await updater.complete(updater.new_agent_message([text(f'got: {value}')]))
            return

        word = value.split(' ')[0]
        if word == 'task':
            await updater.start_work(updater.new_agent_message([text('working')]))
            await updater.add_artifact([text('part1')], artifact_id='a1', name='result')
            await updater.add_artifact([text('part2')], artifact_id='a1', append=True, last_chunk=True)
            await updater.complete(updater.new_agent_message([text('done')]))
        elif word == 'input':
            await updater.requires_input(updater.new_agent_message([text('need more')]))
        elif word == 'slow':
            await updater.start_work()
            try:
                await asyncio.sleep(3600)
            except asyncio.CancelledError:
                # a2a-sdk 0.3 bug workaround: tasks/cancel hands cancel() a tapped child queue, so the CANCELED event
                # never reaches other subscribers (tasks/resubscribe). Publish it on the main queue here as well.
                await updater.cancel()
                raise
        else:
            await updater.complete(updater.new_agent_message([text(value)]))

    async def cancel(self, context: RequestContext, event_queue: EventQueue) -> None:
        await TaskUpdater(event_queue, context.task_id, context.context_id).cancel()


def build(port: int):
    base = f'http://127.0.0.1:{port}'
    card = AgentCard(
        name='Python v0.3 Scenario Agent',
        description='Official a2a-sdk 0.3 agent for interop tests',
        version='1.0.0',
        protocol_version='0.3.0',
        url=base,
        preferred_transport='JSONRPC',
        additional_interfaces=[
            AgentInterface(url=base, transport='JSONRPC'),
            AgentInterface(url=base, transport='HTTP+JSON'),
        ],
        capabilities=AgentCapabilities(streaming=True, push_notifications=False),
        default_input_modes=['text/plain'],
        default_output_modes=['text/plain'],
        skills=[AgentSkill(id='scenario', name='Scenario', description='Interop scenarios', tags=['test'])],
        supports_authenticated_extended_card=True,
    )
    extended = card.model_copy(update={'name': 'Python v0.3 Scenario Agent (extended)'})
    handler = DefaultRequestHandler(agent_executor=ScenarioExecutor(), task_store=InMemoryTaskStore())

    # REST (FastAPI) at /v1/..., and the JSON-RPC route at / added to the same app.
    app = A2ARESTFastAPIApplication(agent_card=card, http_handler=handler, extended_agent_card=extended).build()
    rpc = A2AStarletteApplication(agent_card=card, http_handler=handler, extended_agent_card=extended)
    app.router.routes.extend(rpc.routes(rpc_url='/'))
    return app


if __name__ == '__main__':
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5400
    uvicorn.run(build(port), host='127.0.0.1', port=port, log_level=os.environ.get('LOG', 'warning'))
