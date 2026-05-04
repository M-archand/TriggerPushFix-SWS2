<div align="center">
  <img src="https://pan.samyyc.dev/s/VYmMXE" />
  <h2><strong>TriggerPush Fix</strong></h2>
  <h3>Revert trigger_push behaviour to be like CSGO</h3>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <img src="https://img.shields.io/github/downloads/M-archand/TriggerPushFix-SWS2/total" alt="Downloads">
  <img src="https://img.shields.io/github/stars/M-archand/TriggerPushFix-SWS2?style=flat&logo=github" alt="Stars">
  <img src="https://img.shields.io/github/license/M-archand/TriggerPushFix-SWS2" alt="License">
</p>

## Overview

Place the TriggerPushFix folder in your `addons/swiftlys2/plugins` folder. CSGO trigger_push behavior is enabled globally for all maps by default. Add the `trigger_push_fix 0` ConVar in your server config to disable it globally and instead enable it on a per-map basis.

If `trigger_push_fix 1`, you may disable it for specific maps by adding the map names in `addons/swiftlys2/configs/plugins/TriggerPushFix/config.toml`

If `trigger_push_fix 0`, you may enable it for specific maps by adding the map names in `addons/swiftlys2/configs/plugins/TriggerPushFix/config.toml`

## Config
```
# Maps that override the trigger_push_fix setting here:
Maps = [
    "surf_testmap1",
    "surf_testmap2",
]

### Cache the computed push vector once, or calculate every tick
CachePushVector = true

# If a required game signature fails to resolve, kill all trigger_push
# entities instead of silently falling back to CS2 behaviour
KillOnFailure = true

# Optionally send a Discord notification when a signature fails to resolve
DiscordWebhook = ""
```