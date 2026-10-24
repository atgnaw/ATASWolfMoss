# 只依赖公开 SDK；支持 TradeVant 内置构建和使用已安装 PluginSDK 的独立构建。
cmake_minimum_required(VERSION 3.27)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(deep_v_tracker LANGUAGES CXX)
    find_package(PluginSDK CONFIG REQUIRED)
    find_package(nlohmann_json CONFIG REQUIRED)
endif()

plugin_add_indicator(deep_v_tracker
    INDICATOR_ID deep_v_tracker
    VERSION 0.2.0
    MANIFEST "${CMAKE_CURRENT_SOURCE_DIR}/manifest.json.in"
    SOURCES model.cpp statistics.cpp acceleration.cpp exhaustion.cpp slowdown.cpp profile.cpp levels.cpp engine.cpp
            settings.cpp calculator.cpp calculator_inputs.cpp calculator_records.cpp renderer.cpp plugin.cpp)
target_link_libraries(deep_v_tracker PRIVATE nlohmann_json::nlohmann_json)
