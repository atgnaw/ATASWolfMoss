import unittest

from websocket_config import get_websocket_config


class WebSocketConfigTests(unittest.TestCase):
    def test_defaults_to_localhost(self):
        config = get_websocket_config({})

        self.assertEqual(config["listen_host"], "127.0.0.1")
        self.assertEqual(config["port"], 8766)

    def test_reads_configured_lan_listener(self):
        config = get_websocket_config({
            "websocket": {
                "listen_host": "0.0.0.0",
                "port": 8767,
            }
        })

        self.assertEqual(config["listen_host"], "0.0.0.0")
        self.assertEqual(config["port"], 8767)

    def test_invalid_port_falls_back_to_default(self):
        config = get_websocket_config({
            "websocket": {
                "listen_host": "0.0.0.0",
                "port": 70000,
            }
        })

        self.assertEqual(config["listen_host"], "0.0.0.0")
        self.assertEqual(config["port"], 8766)

    def test_allows_executor_default_port_override(self):
        config = get_websocket_config({}, default_port=8767)

        self.assertEqual(config["listen_host"], "127.0.0.1")
        self.assertEqual(config["port"], 8767)


if __name__ == "__main__":
    unittest.main()
