#!/usr/bin/env python
# -*- coding: utf-8 -*-

"""Fan-out gateway between ATAS and execution backends."""

import asyncio
import json
import logging
import os
import sys
import uuid
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Dict, Mapping, Optional, Set

try:
    import websockets
except ImportError:
    websockets = None

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PARENT_DIR = os.path.dirname(CURRENT_DIR)
if PARENT_DIR not in sys.path:
    sys.path.insert(0, PARENT_DIR)

from sync_contract import parse_target_units


logging.basicConfig(level=logging.INFO, format="%(asctime)s - %(name)s - %(levelname)s - %(message)s")
logger = logging.getLogger(__name__)

BASE_DIR = PARENT_DIR
CONFIG_PATH = os.path.join(BASE_DIR, "config.json")

DEFAULT_GATEWAY_CONFIG = {
    "listen_host": "127.0.0.1",
    "port": 8766,
    "request_timeout_seconds": 30,
}


@dataclass
class RegisteredExecutor:
    executor_id: str
    executor_type: str
    websocket: Any
    capabilities: Set[str] = field(default_factory=set)
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    pending: Dict[str, asyncio.Future] = field(default_factory=dict)


registered_executors: Dict[str, RegisteredExecutor] = {}
url_executor_locks: Dict[str, asyncio.Lock] = {}


def load_config(path: str = CONFIG_PATH) -> Dict[str, Any]:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except FileNotFoundError:
        logger.warning("配置文件不存在，Gateway使用默认配置: %s", path)
        return {}


def get_gateway_config(config: Mapping[str, Any]) -> Dict[str, Any]:
    gateway = config.get("gateway", {})
    if not isinstance(gateway, dict):
        gateway = {}

    listen_host = str(gateway.get("listen_host", DEFAULT_GATEWAY_CONFIG["listen_host"])).strip()
    if not listen_host:
        listen_host = DEFAULT_GATEWAY_CONFIG["listen_host"]

    try:
        port = int(gateway.get("port", DEFAULT_GATEWAY_CONFIG["port"]))
    except (TypeError, ValueError):
        port = DEFAULT_GATEWAY_CONFIG["port"]
    if port <= 0 or port > 65535:
        port = DEFAULT_GATEWAY_CONFIG["port"]

    try:
        timeout = float(gateway.get("request_timeout_seconds", DEFAULT_GATEWAY_CONFIG["request_timeout_seconds"]))
    except (TypeError, ValueError):
        timeout = DEFAULT_GATEWAY_CONFIG["request_timeout_seconds"]
    if timeout <= 0:
        timeout = DEFAULT_GATEWAY_CONFIG["request_timeout_seconds"]

    return {
        "listen_host": listen_host,
        "port": port,
        "request_timeout_seconds": timeout,
    }


def get_targets_config(config: Mapping[str, Any]) -> Dict[str, Dict[str, Any]]:
    targets = config.get("targets", {})
    if not isinstance(targets, dict):
        return {}

    normalized: Dict[str, Dict[str, Any]] = {}
    for executor_id, target in targets.items():
        if not isinstance(target, dict):
            continue
        normalized[str(executor_id)] = dict(target)

    return normalized


def aggregate_executor_results(results: Mapping[str, Mapping[str, Any]]) -> str:
    if not results:
        return "error"

    successes = sum(1 for result in results.values() if result.get("status") == "success")
    if successes == len(results):
        return "success"
    if successes > 0:
        return "partial_error"
    return "error"


async def dispatch_to_executors(
    request: Mapping[str, Any],
    targets: Mapping[str, Mapping[str, Any]],
    registered_executor_ids: Set[str],
    timeout: float,
    send_to_registered: Callable[[str, Mapping[str, Any], float], Awaitable[Dict[str, Any]]],
    send_to_url: Callable[[str, str, Mapping[str, Any], float], Awaitable[Dict[str, Any]]],
) -> Dict[str, Any]:
    enabled_targets = {
        executor_id: dict(target)
        for executor_id, target in targets.items()
        if bool(target.get("enabled", True))
    }

    params = request.get("params", {})
    symbol = params.get("symbol") if isinstance(params, Mapping) else None
    target_units: Optional[int] = None
    if isinstance(params, Mapping) and "net_volume" in params:
        target_units = parse_target_units(params.get("net_volume"))

    logger.info(
        "Gateway收到sync_position: symbol=%s, target_units=%s, enabled_targets=%s, registered=%s",
        symbol,
        target_units,
        sorted(enabled_targets.keys()),
        sorted(registered_executor_ids),
    )

    async def call_target(executor_id: str, target: Mapping[str, Any]):
        try:
            if target.get("connection") == "registered":
                if executor_id not in registered_executor_ids:
                    return executor_id, {
                        "status": "error",
                        "message": f"执行端未注册: {executor_id}",
                    }
                result = await asyncio.wait_for(
                    send_to_registered(executor_id, request, timeout),
                    timeout=timeout,
                )
                return executor_id, result

            url = str(target.get("url", "")).strip()
            if not url:
                return executor_id, {
                    "status": "error",
                    "message": f"执行端未配置url: {executor_id}",
                }
            result = await asyncio.wait_for(
                send_to_url(executor_id, url, request, timeout),
                timeout=timeout,
            )
            return executor_id, result
        except asyncio.TimeoutError:
            return executor_id, {
                "status": "error",
                "message": f"执行端超时: {executor_id}",
            }
        except Exception as exc:
            return executor_id, {
                "status": "error",
                "message": f"执行端异常: {executor_id}: {exc}",
            }

    pairs = await asyncio.gather(
        *[call_target(executor_id, target) for executor_id, target in enabled_targets.items()]
    )
    results = {executor_id: result for executor_id, result in pairs}
    status = aggregate_executor_results(results)

    logger.info(
        "Gateway同步结果: status=%s, results=%s",
        status,
        {
            executor_id: {
                "status": result.get("status"),
                "message": result.get("message"),
            }
            for executor_id, result in results.items()
        },
    )

    return {
        "status": status,
        "message": "执行端同步结果汇总",
        "data": {
            "target_units": target_units,
            "results": results,
        },
    }


async def send_request_over_websocket(websocket: Any, request: Mapping[str, Any], timeout: float) -> Dict[str, Any]:
    await websocket.send(json.dumps(request, ensure_ascii=False))
    raw_response = await asyncio.wait_for(websocket.recv(), timeout=timeout)
    response = json.loads(raw_response)
    if not isinstance(response, dict):
        return {"status": "error", "message": "执行端返回非对象JSON"}
    return response


async def send_to_registered_executor(executor_id: str, request: Mapping[str, Any], timeout: float) -> Dict[str, Any]:
    executor = registered_executors[executor_id]
    if "sync_position" not in executor.capabilities:
        return {
            "status": "error",
            "message": f"执行端不支持sync_position: {executor_id}",
        }

    request_id = f"gw-{executor_id}-{uuid.uuid4()}"
    gateway_request = {
        "id": request_id,
        "action": request.get("action"),
        "params": request.get("params", {}),
    }
    async with executor.lock:
        loop = asyncio.get_running_loop()
        future = loop.create_future()
        executor.pending[request_id] = future
        try:
            await executor.websocket.send(json.dumps(gateway_request, ensure_ascii=False))
            return await asyncio.wait_for(future, timeout=timeout)
        finally:
            executor.pending.pop(request_id, None)


async def send_to_url_executor(
    executor_id: str,
    url: str,
    request: Mapping[str, Any],
    timeout: float,
) -> Dict[str, Any]:
    gateway_request = {
        "id": f"gw-{executor_id}-{uuid.uuid4()}",
        "action": request.get("action"),
        "params": request.get("params", {}),
    }
    async def call_executor():
        if websockets is None:
            return {
                "status": "error",
                "message": "缺少Python依赖: websockets",
            }
        async with websockets.connect(url, ping_interval=30, ping_timeout=max(timeout, 30)) as websocket:
            try:
                await asyncio.wait_for(websocket.recv(), timeout=2)
            except Exception:
                pass
            return await send_request_over_websocket(websocket, gateway_request, timeout)

    return await run_with_url_executor_lock(executor_id, call_executor)


async def run_with_url_executor_lock(
    executor_id: str,
    operation: Callable[[], Awaitable[Dict[str, Any]]],
) -> Dict[str, Any]:
    lock = url_executor_locks.get(executor_id)
    if lock is None:
        lock = asyncio.Lock()
        url_executor_locks[executor_id] = lock

    async with lock:
        return await operation()


async def register_executor(websocket: Any, params: Mapping[str, Any]) -> Dict[str, Any]:
    executor_id = str(params.get("executor_id", "")).strip()
    executor_type = str(params.get("executor_type", executor_id)).strip()
    capabilities_raw = params.get("capabilities", [])
    capabilities = {str(capability) for capability in capabilities_raw if capability}

    if not executor_id:
        return {"status": "error", "message": "缺少executor_id"}

    registered_executors[executor_id] = RegisteredExecutor(
        executor_id=executor_id,
        executor_type=executor_type,
        websocket=websocket,
        capabilities=capabilities,
    )
    logger.info("执行端已注册: id=%s, type=%s, capabilities=%s", executor_id, executor_type, sorted(capabilities))
    return {
        "status": "success",
        "message": f"执行端已注册: {executor_id}",
        "data": {
            "executor_id": executor_id,
            "executor_type": executor_type,
            "capabilities": sorted(capabilities),
        },
    }


def unregister_websocket(websocket: Any) -> None:
    stale_ids = [
        executor_id
        for executor_id, executor in registered_executors.items()
        if executor.websocket == websocket
    ]
    for executor_id in stale_ids:
        logger.info("执行端已断开注册: %s", executor_id)
        for future in registered_executors[executor_id].pending.values():
            if not future.done():
                future.set_result({"status": "error", "message": "执行端连接已断开"})
        del registered_executors[executor_id]


def find_executor_by_websocket(websocket: Any) -> Optional[RegisteredExecutor]:
    for executor in registered_executors.values():
        if executor.websocket == websocket:
            return executor
    return None


def maybe_handle_executor_response(websocket: Any, message: Mapping[str, Any]) -> bool:
    executor = find_executor_by_websocket(websocket)
    if executor is None:
        return False

    response_id = message.get("id")
    if not response_id:
        return False

    future = executor.pending.get(str(response_id))
    if future is None:
        return False

    if not future.done():
        future.set_result(dict(message))
    return True


async def handle_decoded_message(websocket: Any, request: Mapping[str, Any], config: Mapping[str, Any]) -> None:
    action = request.get("action")
    params = request.get("params", {})

    if action == "register_executor":
        response = await register_executor(websocket, params)
    elif action == "health_check":
        response = {
            "status": "success",
            "message": "Gateway运行中",
            "data": {"registered_executors": sorted(registered_executors.keys())},
        }
    elif action == "sync_position":
        gateway_config = get_gateway_config(config)
        response = await dispatch_to_executors(
            request=request,
            targets=get_targets_config(config),
            registered_executor_ids=set(registered_executors.keys()),
            timeout=float(gateway_config["request_timeout_seconds"]),
            send_to_registered=send_to_registered_executor,
            send_to_url=send_to_url_executor,
        )
    else:
        response = {"status": "error", "message": f"未知操作: {action}"}

    if "id" in request:
        response["id"] = request["id"]
    await websocket.send(json.dumps(response, ensure_ascii=False))


async def handle_message(websocket: Any, message: str, config: Mapping[str, Any]) -> None:
    try:
        request = json.loads(message)
        if not isinstance(request, dict):
            await websocket.send(json.dumps({"status": "error", "message": "JSON必须是对象"}, ensure_ascii=False))
            return

        if maybe_handle_executor_response(websocket, request):
            return

        await handle_decoded_message(websocket, request, config)
    except json.JSONDecodeError:
        await websocket.send(json.dumps({"status": "error", "message": "无效JSON格式"}, ensure_ascii=False))
    except Exception as exc:
        logger.exception("处理Gateway消息异常: %s", exc)
        await websocket.send(json.dumps({"status": "error", "message": f"处理请求异常: {exc}"}, ensure_ascii=False))


async def websocket_handler(websocket: Any) -> None:
    client_address = getattr(websocket, "remote_address", None)
    logger.info("Gateway client connected: %s", client_address)
    config = load_config()

    try:
        await websocket.send(json.dumps({
            "status": "success",
            "message": "connected to ATAS execution gateway",
            "registered_executors": sorted(registered_executors.keys()),
        }, ensure_ascii=False))

        async for message in websocket:
            await handle_message(websocket, message, config)
    except Exception as exc:
        if websockets is not None and isinstance(exc, websockets.exceptions.ConnectionClosed):
            logger.info("Gateway client disconnected: %s", client_address)
        else:
            raise
    finally:
        unregister_websocket(websocket)


async def start_gateway() -> None:
    if websockets is None:
        raise RuntimeError("Missing Python dependency: websockets")

    config = load_config()
    gateway_config = get_gateway_config(config)
    host = gateway_config["listen_host"]
    port = gateway_config["port"]

    logger.info("Starting ATAS execution gateway ws://%s:%s", host, port)
    await websockets.serve(
        websocket_handler,
        host,
        port,
        ping_interval=60,
        ping_timeout=180,
        max_size=10 * 1024 * 1024,
        max_queue=1024,
        close_timeout=60,
    )
    await asyncio.Future()


if __name__ == "__main__":
    try:
        asyncio.run(start_gateway())
    except KeyboardInterrupt:
        logger.info("Gateway closed")
