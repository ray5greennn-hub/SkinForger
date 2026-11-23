# SkinForger FPS Protection Test Results

Date: 2026-09-27

## Build

- Runtime build: passed, 0 warnings, 0 errors.
- Patch builder: passed, 0 warnings, 0 errors.
- Config/cap tests: 13 passed.
- Static IL check: 412 existing methods preserved.
- No reachable FPS Update guard, background worker, coroutine, or VSync setter.

## Runtime

- Active DLL SHA256: `2C8B60D016D0785B45D870C7ECF73A45E14F406D40ECB4C1BB6D187AB34D346D`
- Early initialization: `targetFrameRate -1 -> 60`, `vSyncCount=0` unchanged.
- Loading scene correction: `targetFrameRate 240 -> 60`, `vSyncCount=1` unchanged.
- Duplicate early initialization log: absent.
- Runtime frame audit: 30.132 seconds, 1571 frames, average 52.138 FPS, final target 60.
- Stable CPU sample: 2.71% normalized over 10 seconds.
- Intel Iris Xe 3D: 24.08% average, 25.75% peak over five samples.
- Stable thermal zone temperature: 60.1 C.
- Throttle reasons: 0.
- Exit: process ended through Steam; DLL exclusive-open test passed.

## Notes

- The exact highest rendered startup FPS was not measured with an external presentation tool.
- Muse Dash still attempts to set a 240 FPS target during Loading initialization; the event-driven scene callback restores 60 without polling.
- Existing `RegisterTypeInIl2Cpp` cast errors and the Info+ network error predate and are unrelated to FPS protection.
