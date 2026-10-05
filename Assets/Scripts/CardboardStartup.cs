// Disabled: the Google Cardboard XR Plugin was removed from this project.
//
// This file used to hold CardboardStartup, which called into Google.XR.Cardboard to scan the
// viewer's QR code and handle the gear / close buttons. The plugin's last official release
// targets Unity 2019.4; on Unity 6.3 the app built but died on a black screen within three
// seconds, twice, under both GameActivity and the classic Activity entry point.
//
// Stereo rendering and head tracking are now handled by Assets/Scripts/SimpleStereoVR.cs,
// which uses nothing but two cameras and the phone's attitude sensor.
//
// The class is gone rather than the file, because the `using Google.XR.Cardboard;` it needed
// no longer resolves once the package is out of the manifest.
//
// Nothing references it. Safe to delete this file from the Project window.
