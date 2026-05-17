"""WebSocket server configuration helpers."""

from typing import Any, Dict


DEFAULT_WEBSOCKET_HOST = "127.0.0.1"
DEFAULT_WEBSOCKET_PORT = 8766


def get_websocket_config(config: Dict[str, Any]) -> Dict[str, Any]:
    websocket_config = config.get("websocket", {})
    if not isinstance(websocket_config, dict):
        websocket_config = {}

    listen_host = str(websocket_config.get("listen_host", DEFAULT_WEBSOCKET_HOST)).strip()
    if not listen_host:
        listen_host = DEFAULT_WEBSOCKET_HOST

    try:
        port = int(websocket_config.get("port", DEFAULT_WEBSOCKET_PORT))
    except (TypeError, ValueError):
        port = DEFAULT_WEBSOCKET_PORT

    if port <= 0 or port > 65535:
        port = DEFAULT_WEBSOCKET_PORT

    return {
        "listen_host": listen_host,
        "port": port,
    }
