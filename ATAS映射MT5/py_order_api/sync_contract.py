"""Validation helpers for the ATAS net-position sync WebSocket contract."""


def parse_target_units(net_volume) -> int:
    """Convert an ATAS net volume JSON value into an integer target unit count."""
    try:
        numeric_value = float(net_volume)
    except (TypeError, ValueError) as exc:
        raise ValueError("net_volume must be numeric") from exc

    if not numeric_value.is_integer():
        raise ValueError("net_volume must be an integer number of ATAS units")

    return int(numeric_value)
