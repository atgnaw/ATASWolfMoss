import unittest

from sync_contract import format_mt5_comment, parse_target_units


class SyncContractTests(unittest.TestCase):
    def test_accepts_integer_json_numbers(self):
        self.assertEqual(parse_target_units(2), 2)
        self.assertEqual(parse_target_units(-3.0), -3)

    def test_rejects_fractional_net_volume(self):
        with self.assertRaises(ValueError):
            parse_target_units(1.5)

    def test_formats_mt5_comment_with_safe_length_and_characters(self):
        comment = format_mt5_comment("ATAS_SYNC", "Micro E-mini Nasdaq-100")

        self.assertLessEqual(len(comment), 24)
        self.assertRegex(comment, r"^ATAS_SYNC_[A-Za-z0-9_]+$")
        self.assertNotIn("|", comment)
        self.assertNotIn(" ", comment)


if __name__ == "__main__":
    unittest.main()
