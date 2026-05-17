"""Validation helpers for the ATAS net-position sync WebSocket contract."""

import hashlib
import re


MT5_COMMENT_MAX_LENGTH = 24


def parse_target_units(net_volume) -> int:
    """Convert an ATAS net volume JSON value into an integer target unit count."""
    try:
        numeric_value = float(net_volume)
    except (TypeError, ValueError) as exc:
        raise ValueError("net_volume must be numeric") from exc

    if not numeric_value.is_integer():
        raise ValueError("net_volume must be an integer number of ATAS units")

    return int(numeric_value)


def format_mt5_comment(prefix: str, source_symbol: str = "", max_length: int = MT5_COMMENT_MAX_LENGTH) -> str:
    """Build a conservative MT5 order comment.

    Some MT5 terminals/brokers reject long comments or punctuation-heavy values.
    Keep comments short, ASCII-only, and prefix-stable so copier positions can
    still be identified by comment prefix.
    """
    safe_prefix = re.sub(r"[^A-Za-z0-9_]+", "_", str(prefix)).strip("_") or "ATAS_SYNC"
    safe_prefix = safe_prefix[:max_length]
    if not source_symbol:
        return safe_prefix

    safe_source = re.sub(r"[^A-Za-z0-9_]+", "_", str(source_symbol)).strip("_")
    if not safe_source:
        return safe_prefix

    digest = hashlib.sha1(str(source_symbol).encode("utf-8")).hexdigest()[:6]
    available = max_length - len(safe_prefix) - len(digest) - 2
    if available <= 0:
        return safe_prefix

    source_part = safe_source[:available].strip("_") or digest
    return f"{safe_prefix}_{source_part}_{digest}"[:max_length]
