// Avalonia Headless has one dispatcher for the whole process: two test classes that build real windows and render
// them (Shots.cs's SurfaceShots, and StateShots here) can't do that on two threads at once, so test collections run
// one at a time. Everything else here is fast enough that this costs nothing worth noticing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
