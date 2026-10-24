# 仅依赖公开 SDK；可在主工程或已安装 SDK 的独立插件工程中构建。
cmake_minimum_required(VERSION 3.27)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(deep_m_effort LANGUAGES CXX)
    find_package(PluginSDK CONFIG REQUIRED)
    find_package(nlohmann_json CONFIG REQUIRED)
endif()

plugin_add_indicator(deep_m_effort
    INDICATOR_ID deep_m_effort
    VERSION 0.1.1
    MANIFEST "${CMAKE_CURRENT_SOURCE_DIR}/manifest.json.in"
    SOURCES settings.cpp engine.cpp engine_validation.cpp zone_lifecycle.cpp calculator.cpp renderer.cpp plugin.cpp)
target_link_libraries(deep_m_effort PRIVATE nlohmann_json::nlohmann_json)
