// Superseded by TrafficRoad.cs / TrafficRing.cs, and removed rather than left in the scene.
//
// This held the whole road network as numbers and generated waypoints from them. Two hundred
// generated points kept landing off the tarmac, because every one of them inherited whatever
// small error was in the measured centre, heading and length - and a waypoint a car cannot quite
// reach is a car that drives into the distance and never comes back.
//
// The replacement drops waypoints entirely. A street is one object whose gizmo is drawn from the
// same maths the cars drive on, so aligning a street is a drag with the Move and Rotate tools
// and "is it right?" is answered by looking at it. See TrafficRoad, TrafficRing and RoadDriver.
//
// The class is gone rather than the file, because this session cannot delete files from the
// project. Nothing references it. Safe to delete from the Project window.
