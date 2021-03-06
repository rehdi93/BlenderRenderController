## Installing

Before starting, you should have the following dependencies installed:

- Blender, obviously.
- [FFmpeg](https://ffmpeg.org/download.html), required for joining the parts together. 

## Requirements

### Windows
- .NET framework 4.6.1 or higher

### Linux (beta)
- .NET core runtime 2.0 or higher
- GTK3 3.18 or higher

Early version, might not work 100%...
   
## How to use
1. Create your Blender VSE project normally within Blender.
 
2. Open Blender Render Controller, browse for the .blend file.
 
3. BRC will automatically calculate the *Start Frame*, *End Frame* and *Chunk Size* according to the length of the project and number of logical cores in your CPU respectively, you can change these values manually if you want.

   - Normally, the N# of processes should match the N# of logical cores in you system for a optimum render performance.
 
4. Choose a joining method:

   - *Join chunks w/ mixdown audio* - renders chunks, makes a separated audio file and joins it all together, recommended if you have audio tracks in your project.
   - *Join chunks* - same as above, minus audio mixdown.
   - *No extra action* - just renders the Chunks.
 
5. Click *Start Render* and wait for the render to be done.
