"""Official a2a-sdk (Python) agent with the same scenarios as the .NET interop agent.

Usage: python py_server.py <port>
Serves JSON-RPC at POST /a2a and HTTP+JSON under /a2a, card at /.well-known/agent-card.json.
"""

import asyncio
import sys

import uvicorn
from starlette.applications import Starlette

from a2a.helpers.proto_helpers import get_message_text, new_text_message, new_text_part
from a2a.server.agent_execution import AgentExecutor, RequestContext
from a2a.server.events import EventQueue
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.routes import create_agent_card_routes, create_jsonrpc_routes, create_rest_routes
from a2a.server.tasks import InMemoryTaskStore, TaskUpdater
from a2a.types import (
    AgentCapabilities,
    AgentCard,
    AgentInterface,
    AgentSkill,
    Task,
    TaskState,
    TaskStatus,
)


class ScenarioExecutor(AgentExecutor):
    async def execute(self, context: RequestContext, event_queue: EventQueue) -> None:
        text = get_message_text(context.message) if context.message else ''
        task = context.current_task

        if text.startswith('hello') and task is None:
            await event_queue.enqueue_event(new_text_message('hi there', context_id=context.context_id))
            return

        updater = TaskUpdater(event_queue, context.task_id, context.context_id)
        if task is None:
            await event_queue.enqueue_event(Task(
                id=context.task_id, context_id=context.context_id,
                status=TaskStatus(state=TaskState.TASK_STATE_SUBMITTED), history=[context.message]))

        if task is not None and task.status.state == TaskState.TASK_STATE_INPUT_REQUIRED:
            await updater.complete(updater.new_agent_message([new_text_part(f'got: {text}')]))
            return

        word = text.split(' ')[0]
        if word == 'task':
            await updater.start_work(updater.new_agent_message([new_text_part('working')]))
            await updater.add_artifact([new_text_part('part1')], artifact_id='a1', name='result')
            await updater.add_artifact([new_text_part('part2')], artifact_id='a1', append=True, last_chunk=True)
            await updater.complete(updater.new_agent_message([new_text_part('done')]))
        elif word == 'input':
            await updater.requires_input(updater.new_agent_message([new_text_part('need more')]))
        elif word == 'slow':
            await updater.start_work()
            await asyncio.sleep(3600)
        else:
            await updater.complete(updater.new_agent_message([new_text_part(text)]))

    async def cancel(self, context: RequestContext, event_queue: EventQueue) -> None:
        updater = TaskUpdater(event_queue, context.task_id, context.context_id)
        await updater.cancel()


def build_app(port: int) -> Starlette:
    base = f'http://127.0.0.1:{port}/a2a'
    card = AgentCard(
        name='Python Scenario Agent',
        description='Official a2a-sdk agent for interop tests',
        version='1.0.0',
        supported_interfaces=[
            AgentInterface(url=base, protocol_binding='JSONRPC', protocol_version='1.0'),
            AgentInterface(url=base, protocol_binding='HTTP+JSON', protocol_version='1.0'),
        ],
        capabilities=AgentCapabilities(streaming=True, push_notifications=False),
        default_input_modes=['text/plain'],
        default_output_modes=['text/plain'],
        skills=[AgentSkill(id='scenario', name='Scenario', description='Interop scenarios', tags=['test'])],
    )
    handler = DefaultRequestHandler(agent_executor=ScenarioExecutor(), task_store=InMemoryTaskStore(), agent_card=card)
    routes = [
        *create_agent_card_routes(card),
        *create_jsonrpc_routes(handler, rpc_url='/a2a'),
        *create_rest_routes(handler, path_prefix='/a2a'),
    ]
    return Starlette(routes=routes)


if __name__ == '__main__':
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5200
    uvicorn.run(build_app(port), host='127.0.0.1', port=port, log_level='warning')
