# Tilted LCD Mirrors and Camera views

Important: Mirror and Camera views also require a Pulsar Plugin to work!
[Get Pulsar](https://github.com/SpaceGT/Pulsar)

Without the plugin the mod still gives you the ability to Pitch, Yaw and Roll LCD Panels. 

The mod also handles camera and mirror settings and configurations without the plugin. 
The Mirror and Camera screens also contains information on how to get the plugin, so others on a server with the mod active can find it.


## LCD Panel Tilt

The mod does a small attempt to keep panels within their cube space but it's up to the user to keep them from clipping into other blocks. 

The mod only move the model a little bit so you will be able to walk through larger tilted panels.

You can only tilt thin panels. Full block variants would always clip into their surrounding.

! Build Vision seem to only trigger where it sees both the physics shape and the model at the same time so it can be a bit tricky to open the BV popup on a tilted panel.

## Mirrors

The mod adds a "Mirror" LCD app. Unfortunately mods are not allowed render the whole world so you will need a plugin to see reflections. 

If you are not running the mod the screen will just show "Mirror" and "Plugin not loaded". Press F on a display with the mirror app to get the link to the plugin loader where you can find the plugin. 

No configuration, it's really just a mirror.

## Cameras

The mod allso add a "Camera" LCD app where you can select a camera to view a stream from. Same story, here; it needs the plugin.

It also adds a Zoom slider to camera blocks, and toolbar actions for zooming in and out. 

Panels running the Camera app have an option to override the cameras own zoom, enabling you to have one wide angle and one zoomed in at the same time. 

The panel also get toolbar commands to zoom in and out, and to switch to the next and previous camera in the list. Connect to a timer block for that surveliance feel.



## The Plugin

You will find the Mirror plugin in Pulsar. 

There are a few settings to play with, that are set pretty low to be as nice to your graphics card as it can. The first thing you want to play with is probably the "Max view distance".

It is important to understand that drawing the game world is the most time consuming and power demanding thing the games does per frame, and this plugin essentially does that one more time every frame a mirror or camera is in view. You will notice your GPU struggle, you will notice frame drops, and your GPU will get warmer and you will get more noise from your GPU fan.

You will also notice mirror and camera view lagging when you have more than one in view at the same time. The plugin tries its best to estimate what you are looking at and refresh that panel more often, so hopefully the one your are looking at is smooth while other stutter a bit.

Mirrors and camera view will also stop rendering completely when you are further away than the Max view distance setting. 

Only mirrors and camera views that are in view and facing you are rendered, but often also those that are behind walls.

Mirrors further away update less often and in lower resolution.



## Setup

1. Subscribe to this Mirror Mod.
2. Download and install [Pulsar the Space Engineers plugin loader](https://github.com/SpaceGT/Pulsar).
3. Starting pulsar starts Space Engineers with Plugin support enabled.
4. Click the "Plugins" button that has appeared in the main menu.
5. Add plugin and find "Mirror".
6. Optional: Add mod and find "Mirror".
7. Pulsar tells you to restart to apply the mod. Do that.
8. Back up your save. This is a pretty new mod and plugin made by someone who never made a mod or plugin before.
9. Optional if you did #6 above: Add Mirror Mod to your save. 
10. Load the save. 
11. Find any block that has a screen in the terminal
12. Set Content to Apps
13. Select Mirror
14. Grab a Clang Cola and some dance emotes and admire yourself in person for the first time!



## Settings
On the pause menu, if you started via Pulsar, you have "Plugins" button.
If you select Mirror and click the cogwheel you get a few settings.
The defaults are set very low in case your GPU struggles.

### Enabled
Uncheck to pause all mirror and camera panels. Quick disable in case GPU struggles.

### Max per frame
This many mirrors will be drawn for each of your main view draw.
I recommend keeping this at 1 unless you have a very fancy GPU.

### Max view distance
You have to be this close for mirrors to update their display. 

### Far clip
This is how far reflections see. Max it out in space if you need to keep a look out for asteroids behind you.

### Distance resoulution LOD
Lowers resolution of mirrors further away. 


### Render on Pause screen
When off mirrors nor panels will render while game is paused. Enable to live preview setting changes.

### Disable shadows
There is a small issue with shadows sometimes flickering that I have not solved yet. Disabling shadows removes the issue but also all reflected darkness.

### Debug HUD
Prints out the update priority scores of all mirror and camera panels in view.


## Limitatinos
Mirrors do not reflect: 
- Themselves
- Other mirrors. They will reflect what **you** last saw in that particular mirror.
- Curved surfaces. Can't really bend light like that. Curved displays behave the same as sloped but the curve distorts the image. 
- Light sources. No disco balls yet unfortunately.


## Avoid just because it looks bad
### Placing mirrors in view of each other.

Mirrors only update when **you** look at them and not when a mirror looks at them.. Yet. 
So the secondary mirror will be frozen at whatever you saw in it last.

### See many mirrors and/or camera displays at the same time
Only one panel will update at a time after a scoring system based on a mix of how much space they occupy on your screen, you and your crosshairs distance from them, if you are standing still or not, time since last update, and some more. 
