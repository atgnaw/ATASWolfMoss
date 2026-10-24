# Deepchart 指标 ATAS 移植

- `deep_m_effort/`：提供的 Deep-M Effort C++ 实现，保持原样。
- `deep_v_tracker/`：提供的 Deep-V Tracker C++ 实现，保持原样。
- `atas/`：ATAS 原生 C# 移植，包含 Deep-M Effort 和 Deep-V Tracker。

安装、计算说明、平台差异和构建步骤见 [Deep-M Effort 说明](atas/README.md) 和 [Deep-V Tracker 说明](atas/DeepVTracker/README.md)。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\atas\build.ps1 -Indicator All
```

产物：`artifacts/DeepMEffort-ATAS-1.0.0.zip`、`artifacts/DeepVTracker-ATAS-1.0.0.zip`。不带 `-Indicator` 时仍默认构建 Deep-M Effort。
