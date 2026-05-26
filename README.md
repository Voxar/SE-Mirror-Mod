# Tilted LCD Mirrors and Cameras

This mod allows you to Pitch, Yaw and Roll your LCD panels by 45 degrees. 

The mod does its best to keep the panels within their cube space but user discretion adviced to not let them clip into other objects.
The mod only alters the location of the visuals and interactions. 


! Build Info seem to only trigger where it sees both the physics shape and the model at the same time so it can be a bit tricky to aim right as the former is not visible at all.


## Mirrors and Cameras? 

The origin of this mod is that I wanted to have proper rear view mirrors in my rovers.
But for those to be useful they need adjustability, and that's what gave birth to this mod.

This Mod somes two LCD additional apps as well.

## Mirror


# Mirrors for Space Engineers!
Can you see yourself playing Space Engineers?

This project is born out of really wanting to play without the third person camera but not without seeing what I am reversing into.


Unfortunately the mirror can not mirror your wonderful helmet on its own. It needs a.. plugin. 
A piece of code that unlocks the games inner world, so the mirror can present you, in your world.


The mod fills a few functions even without its plugin
1. It tells everyone that they should get the plugin!
2. It stores the mirror and camera settings and syncs them over MP!
3. It allows you to tilt LCD screens... !

## Oh right, the tilting! 
That's what makes this mod worth grabbing even without the plugin!

Find Yaw and Pitch sliders in the LCD panel terminal!

Any LCD that is thinner than half a block can be tilted and turned up to 45 degrees (because we don't want things clipping into other stuff).
Amazing for both your car and your ceiling tv! Does not work for inset blocks, seats or cockpits, curved or sloped things except for the corner LCD's.

All because I wanted to have that one adjustable rear view mirror in my cockpit.

# Setup

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



## Advanced

Soooo all I ever wanted was a rear view mirror on my rover. BUT if I can't stop myself from building HUGE ROTATING WALL OF MIRRORS then who will stop you?
SO YOU CAN! WITHOUT EVEN BREAKING YOUR GPU! 
The mod/plugin finds out when you place a bunch of panels next to eachother and does one big render for the whole thing!


## Settings
On the pause menu, if you started via Pulsar, you have that plugins button again.
If you select Mirror and click the cogwheel you get a few settings.
The defaults are set very low in case your GPU struggles.

### Enable
Uncheck to pause all mirror and camera panels. Quick disable in case GPU struggles.

### Max per frame
This many mirrors will be drawn for each of your main view draw.
I recommend keeping this at 1 unless you have a very fancy GPU.

### Max view distance
You have to be this close for mirrors to update their display. 
Conservative default to save GPU's. Feel free to bump up but keep in mind that all mirrors in your angle of view 
will update, even those behind walls. (I have not figured out how to filter those out yet)

### Far clip
This is how far reflections see. Max it out in space if you need to keep a look out for asteroids behind you.

### Render shadows
There is a small issue with shadows flickering I have not solved yet. If you have issues with that disabling this stops shadows from rendering at all 
in mirrors and camera views. Also somewhat of a nightvision hack unfortunately.

### Resoulution LOD
Lowers resolution of the mirror renders and thus speeds things up. 
I recommend keeping it on unless you have weird issues with it. Disabling improves quality a bit when you are far away from mirrors. 

### Debug HUD
Prints out all the info I need to debug. Left it in in case you might be interested.
White lines around panels and render groups, green lines around a panel or group that is prioritized for updated.
Lines of text is each panel/group in order of update priority. Can be useful to understand how things work and to give feedback.


## Some limitatinos
Mirrors do not reflect: 
- Themselves
- Other mirrors correctly. They will instead reflect what **you** last saw in that mirror.
- Curved surfaces correctly. It's... just complicated bending light like that without actually tracing it.
- Light. I mean it does not bounce light. So no disco balls yet :<

## Now some things you probably should not do
I mean you can but for a more plesant experience I recommend that you do not

### Place mirrors in view of each other
They only render when **you** look at them, not when a mirror looks at them.. Yet. 
So the secondary mirror will be frozen at whenever you last looked at it.

### See many mirrors and/or camera displays at the same time
Unless you have a GPU waaay too expensive to just play Space Engineers you want to keep "render per frame" to 1. 
This means only one mirror in view will be smooth, the rest will lag a bit. At 120 it's not very noticable with 2 or 3, 
but after that it gets choppy. I try to guess which one mirror or camera screen you are looking at and keep that closest to realtime so have your crosshair on top of that one.

uh, well that's it! 



# Thank you

Communities
- Pulsar
- Splitsie

[AdjustableLCDs Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=2427400629)
Showing how to tilt screens without a plugin. Grab this for extreme flexibility in LCD placement
