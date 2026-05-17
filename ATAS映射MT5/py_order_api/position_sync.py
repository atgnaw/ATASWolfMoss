"""Pure planning helpers for ATAS net-position to MT5 unit-ticket sync."""

from typing import Any, Dict, Iterable, List


BUY = "BUY"
SELL = "SELL"


def _normalise_type(position: Dict[str, Any]) -> str:
    position_type = str(position.get("type", "")).upper()
    if position_type not in (BUY, SELL):
        raise ValueError(f"Unsupported position type: {position_type}")
    return position_type


def _sorted_positions(positions: Iterable[Dict[str, Any]], order_type: str) -> List[Dict[str, Any]]:
    filtered = [position for position in positions if _normalise_type(position) == order_type]
    return sorted(filtered, key=lambda position: int(position["ticket"]))


def _close_actions(positions: Iterable[Dict[str, Any]]) -> List[Dict[str, Any]]:
    actions = []
    for position in sorted(positions, key=lambda item: int(item["ticket"]), reverse=True):
        actions.append(
            {
                "action": "close",
                "ticket": int(position["ticket"]),
                "order_type": _normalise_type(position),
                "volume": float(position["volume"]),
            }
        )
    return actions


def _open_actions(order_type: str, units: int, unit_volume: float) -> List[Dict[str, Any]]:
    return [
        {"action": "open", "order_type": order_type, "volume": float(unit_volume)}
        for _ in range(units)
    ]


def plan_unit_sync(
    current_positions: Iterable[Dict[str, Any]],
    target_units: int,
    unit_volume: float,
) -> List[Dict[str, Any]]:
    """Plan close/open actions to make MT5 copy tickets match ATAS net units.

    Each ATAS unit maps to one MT5 ticket with ``unit_volume`` lots. The planner
    never opens an opposite ticket to reduce exposure; it closes conflicting or
    excess tickets first, then opens missing target-direction tickets.
    """
    if int(target_units) != target_units:
        raise ValueError("target_units must be an integer")
    if unit_volume <= 0:
        raise ValueError("unit_volume must be greater than zero")

    positions = list(current_positions)
    buys = _sorted_positions(positions, BUY)
    sells = _sorted_positions(positions, SELL)

    if target_units == 0:
        return _close_actions(buys + sells)

    if target_units > 0:
        actions = _close_actions(sells)
        target_count = int(target_units)
        if len(buys) > target_count:
            actions.extend(_close_actions(buys[target_count:]))
        elif len(buys) < target_count:
            actions.extend(_open_actions(BUY, target_count - len(buys), unit_volume))
        return actions

    actions = _close_actions(buys)
    target_count = abs(int(target_units))
    if len(sells) > target_count:
        actions.extend(_close_actions(sells[target_count:]))
    elif len(sells) < target_count:
        actions.extend(_open_actions(SELL, target_count - len(sells), unit_volume))
    return actions


def net_units(current_positions: Iterable[Dict[str, Any]]) -> int:
    """Return BUY ticket count minus SELL ticket count for copy positions."""
    units = 0
    for position in current_positions:
        position_type = _normalise_type(position)
        units += 1 if position_type == BUY else -1
    return units
