// Superseded by TrafficNetworkBuilder.cs, and removed rather than left in the Tools menu.
//
// This tool built routes out of the existing w1 markers. That was the wrong idea: fourteen of
// the thirty markers sit inside the roundabout, so "the centre" collapsed to a single pair of
// points and every one of the six roads was routed through it. Thirty-six cars then converged on
// one spot, which is the opposite of organised traffic.
//
// The replacement measures the road layout instead of reading the markers as a path - it solves
// for where the six roads intersect, then GENERATES clean waypoints: a closed racetrack along
// each road (out, U-turn, back, U-turn) and a separate circle around the roundabout. Roads and
// the ring never touch, so nothing merges and nothing can gridlock.
//
//   Tools > Emergency VR > Build Traffic Network
//   Tools > Emergency VR > Rebuild Traffic Routes
//
// The class is gone rather than the file, because this session cannot delete files from the
// project. Nothing references it. Safe to delete from the Project window.
