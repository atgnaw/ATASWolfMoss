package com.atasbridge.bookmap.gateway;

public final class GatewayRegistrationMessage {
    private GatewayRegistrationMessage() {
    }

    public static String json() {
        return """
            {
              "action": "register_executor",
              "params": {
                "executor_id": "bookmap",
                "executor_type": "bookmap",
                "capabilities": ["sync_position"]
              }
            }
            """;
    }
}
