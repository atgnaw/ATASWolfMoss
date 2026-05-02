import unittest
from pathlib import Path

from symbol_mapper import SymbolMapper


class SymbolMapperUnitVolumeTests(unittest.TestCase):
    FIXTURES = Path(__file__).parent / "fixtures"

    def test_reads_unit_volume_when_present(self):
        mapper = SymbolMapper(str(self.FIXTURES / "unit_volume_config.json"))

        self.assertEqual(mapper.map_to_mt5("Gold"), "XAUUSD")
        self.assertEqual(mapper.get_unit_volume("Gold"), 0.1)
        self.assertEqual(mapper.get_volume_ratio("Gold"), 0.1)

    def test_falls_back_to_legacy_volume_ratio(self):
        mapper = SymbolMapper(str(self.FIXTURES / "legacy_volume_ratio_config.json"))

        self.assertEqual(mapper.get_unit_volume("BTCUSDT"), 0.01)
        self.assertEqual(mapper.map_volume("BTCUSDT", 2), 0.02)


if __name__ == "__main__":
    unittest.main()
