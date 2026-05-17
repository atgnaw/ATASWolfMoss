# ATAS Bookmap Bridge

This module is the Bookmap-side executor for the ATAS execution gateway.

The intended production shape is:

```text
ATAS ChartStrategy -> Python execution_gateway.py -> Bookmap Java Add-on -> Bookmap Api.sendOrder(...)
```

The current project includes the dependency-light bridge core, net-position planner,
and Gateway registration message. The Bookmap API wrapper should be completed inside
Bookmap's Java add-on lifecycle after confirming the exact API version installed with
the user's Bookmap build.

## Build

Install a JDK and Gradle, then run:

```powershell
cd bookmap_addon
gradle jar
```

The target JAR is:

```text
bookmap_addon/build/libs/ATASBookmapBridge-0.1.0.jar
```

Load it in Bookmap from:

```text
Settings -> Api plugins configuration -> Add
```

## Execution Model

Bookmap futures accounts are net-position based:

```text
delta = ATAS target position - current Bookmap position
delta > 0 -> BUY delta * unitMultiplier
delta < 0 -> SELL abs(delta) * unitMultiplier
delta = 0 -> no order
```

Keep `dryRun=true` until Gateway registration, symbol mapping, current position
reading, and simulated order execution are verified in Bookmap.
