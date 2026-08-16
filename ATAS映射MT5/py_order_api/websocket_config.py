"""WebSocket server configuration helpers."""

from typing import Any, Dict


DEFAULT_WEBSOCKET_HOST = "127.0.0.1"
DEFAULT_WEBSOCKET_PORT = 8766


def get_websocket_config(
    config: Dict[str, Any],
    default_host: str = DEFAULT_WEBSOCKET_HOST,
    default_port: int = DEFAULT_WEBSOCKET_PORT,
) -> Dict[str, Any]:
    websocket_config = config.get("websocket", {})
    if not isinstance(websocket_config, dict):
        websocket_config = {}

    listen_host = str(websocket_config.get("listen_host", default_host)).strip()
    if not listen_host:
        listen_host = default_host

    try:
        port = int(websocket_config.get("port", default_port))
    except (TypeError, ValueError):
        port = default_port

    if port <= 0 or port > 65535:
        port = default_port

    return {
        "listen_host": listen_host,
        "port": port,
    }
