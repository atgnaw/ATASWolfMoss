import asyncio
import unittest

from gateway.execution_gateway import (
    aggregate_executor_results,
    dispatch_to_executors,
    run_with_url_executor_lock,
)


class ExecutionGatewayAggregationTests(unittest.TestCase):
    def test_all_executor_successes_return_success(self):
        status = aggregate_executor_results({
            "mt5": {"status": "success"},
            "bookmap": {"status": "success"},
        })

        self.assertEqual(status, "success")

    def test_mixed_executor_results_return_partial_error(self):
        status = aggregate_executor_results({
            "mt5": {"status": "success"},
            "bookmap": {"status": "error"},
        })

        self.assertEqual(status, "partial_error")

    def test_no_executor_results_return_error(self):
        status = aggregate_executor_results({})

        self.assertEqual(status, "error")

    def test_all_executor_failures_return_error(self):
        status = aggregate_executor_results({
            "mt5": {"status": "error"},
            "bookmap": {"status": "error"},
        })

        self.assertEqual(status, "error")


class ExecutionGatewayDispatchTests(unittest.IsolatedAsyncioTestCase):
    async def test_dispatches_to_registered_and_url_executors(self):
        async def send_to_registered(executor_id, request, timeout):
            return {"status": "success", "message": f"{executor_id} ok"}

        async def send_to_url(executor_id, url, request, timeout):
            return {"status": "success", "message": f"{executor_id} ok"}

        response = await dispatch_to_executors(
            request={"id": "1", "action": "sync_position", "params": {"net_volume": 2}},
            targets={
                "mt5": {"enabled": True, "url": "ws://127.0.0.1:8767"},
                "bookmap": {"enabled": True, "connection": "registered"},
            },
            registered_executor_ids={"bookmap"},
            timeout=1,
            send_to_registered=send_to_registered,
            send_to_url=send_to_url,
        )

        self.assertEqual(response["status"], "success")
        self.assertEqual(set(response["data"]["results"].keys()), {"mt5", "bookmap"})
        self.assertEqual(response["data"]["target_units"], 2)

    async def test_registered_executor_missing_is_reported_as_error(self):
        async def send_to_registered(executor_id, request, timeout):
            raise AssertionError("should not be called")

        async def send_to_url(executor_id, url, request, timeout):
            return {"status": "success"}

        response = await dispatch_to_executors(
            request={"id": "1", "action": "sync_position", "params": {"net_volume": 1}},
            targets={
                "bookmap": {"enabled": True, "connection": "registered"},
            },
            registered_executor_ids=set(),
            timeout=1,
            send_to_registered=send_to_registered,
            send_to_url=send_to_url,
        )

        self.assertEqual(response["status"], "error")
        self.assertEqual(response["data"]["results"]["bookmap"]["status"], "error")

    async def test_executor_timeout_does_not_block_other_executors(self):
        async def send_to_registered(executor_id, request, timeout):
            await asyncio.sleep(timeout + 0.1)
            return {"status": "success"}

        async def send_to_url(executor_id, url, request, timeout):
            return {"status": "success"}

        response = await dispatch_to_executors(
            request={"id": "1", "action": "sync_position", "params": {"net_volume": 1}},
            targets={
                "mt5": {"enabled": True, "url": "ws://127.0.0.1:8767"},
                "bookmap": {"enabled": True, "connection": "registered"},
            },
            registered_executor_ids={"bookmap"},
            timeout=0.01,
            send_to_registered=send_to_registered,
            send_to_url=send_to_url,
        )

        self.assertEqual(response["status"], "partial_error")
        self.assertEqual(response["data"]["results"]["mt5"]["status"], "success")
        self.assertEqual(response["data"]["results"]["bookmap"]["status"], "error")

    async def test_url_executor_calls_are_serialized_per_executor(self):
        active_calls = 0
        max_active_calls = 0

        async def protected_call():
            nonlocal active_calls, max_active_calls
            active_calls += 1
            max_active_calls = max(max_active_calls, active_calls)
            await asyncio.sleep(0.01)
            active_calls -= 1
            return {"status": "success"}

        results = await asyncio.gather(
            run_with_url_executor_lock("mt5", protected_call),
            run_with_url_executor_lock("mt5", protected_call),
        )

        self.assertEqual([result["status"] for result in results], ["success", "success"])
        self.assertEqual(max_active_calls, 1)


if __name__ == "__main__":
    unittest.main()
