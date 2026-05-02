import unittest

from sync_contract import parse_target_units


class SyncContractTests(unittest.TestCase):
    def test_accepts_integer_json_numbers(self):
        self.assertEqual(parse_target_units(2), 2)
        self.assertEqual(parse_target_units(-3.0), -3)

    def test_rejects_fractional_net_volume(self):
        with self.assertRaises(ValueError):
            parse_target_units(1.5)


if __name__ == "__main__":
    unittest.main()
