# Changelog

## 2026-09-06

- Camera app terminal controls (camera source, zoom override, zoom) now
  also appear on Inset Button Panels, Medical Rooms and Refill Stations,
  Store Blocks and ATMs, Custom Turret Controllers and the Console.
- Fix: the camera source list now includes cameras on docked grids, the
  same construct the terminal shows. Next/Previous Camera cycle through
  them too.
- Fix: a camera feed stops when its grid leaves the construct (undock,
  grid split); the panel shows "Camera offline" and the feed returns on
  re-dock without re-selecting.
- The camera splash keeps the camera's name while it is offline, and
  remembers the name across reloads for cameras that can't be found.

## 2026-06-04

- Fix: panel camera selection survives world reload.
