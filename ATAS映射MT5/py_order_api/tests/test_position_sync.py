import unittest

from position_sync import plan_unit_sync


class PositionSyncPlanTests(unittest.TestCase):
    def test_opens_one_mt5_ticket_per_target_unit(self):
        actions = plan_unit_sync([], target_units=2, unit_volume=0.1)

        self.assertEqual(
            actions,
            [
                {"action": "open", "order_type": "BUY", "volume": 0.1},
                {"action": "open", "order_type": "BUY", "volume": 0.1},
            ],
        )

    def test_reduces_existing_long_by_closing_one_ticket(self):
        current = [
            {"ticket": 101, "type": "BUY", "volume": 0.1},
            {"ticket": 102, "type": "BUY", "volume": 0.1},
        ]

        actions = plan_unit_sync(current, target_units=1, unit_volume=0.1)

        self.assertEqual(
            actions,
            [{"action": "close", "ticket": 102, "order_type": "BUY", "volume": 0.1}],
        )

    def test_reverses_from_long_to_short_by_closing_longs_then_opening_short(self):
        current = [
            {"ticket": 101, "type": "BUY", "volume": 0.1},
            {"ticket": 102, "type": "BUY", "volume": 0.1},
        ]

        actions = plan_unit_sync(current, target_units=-1, unit_volume=0.1)

        self.assertEqual(
            actions,
            [
                {"action": "close", "ticket": 102, "order_type": "BUY", "volume": 0.1},
                {"action": "close", "ticket": 101, "order_type": "BUY", "volume": 0.1},
                {"action": "open", "order_type": "SELL", "volume": 0.1},
            ],
        )

    def test_flattens_existing_short_positions(self):
        current = [
            {"ticket": 201, "type": "SELL", "volume": 0.1},
            {"ticket": 202, "type": "SELL", "volume": 0.1},
            {"ticket": 203, "type": "SELL", "volume": 0.1},
        ]

        actions = plan_unit_sync(current, target_units=0, unit_volume=0.1)

        self.assertEqual(
            actions,
            [
                {"action": "close", "ticket": 203, "order_type": "SELL", "volume": 0.1},
                {"action": "close", "ticket": 202, "order_type": "SELL", "volume": 0.1},
                {"action": "close", "ticket": 201, "order_type": "SELL", "volume": 0.1},
            ],
        )


if __name__ == "__main__":
    unittest.main()
