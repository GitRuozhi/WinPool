# KS / StatSys

Hardware and Storage Spaces information collection module.

- `StatSys.py` is the reusable collector.
- `app.py` is a Flask prototype.
- `Full.yaml` controls collection scope.

Captured machine snapshots belong in `Tests\\00_Environment`; this directory contains source code, configuration, and packaging assets only.

The collector is the strongest current source for WinPool's planned read-only hardware and Storage Spaces discovery milestone. The C#/Python integration boundary has not yet been selected. `app.py` remains an incomplete Flask prototype, not a finished GUI or supported service.
