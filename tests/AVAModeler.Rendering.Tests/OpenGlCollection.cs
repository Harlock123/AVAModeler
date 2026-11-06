namespace AVAModeler.Rendering.Tests;

/// <summary>
/// The test classes that draw on a real OpenGL context (<see cref="OffscreenGl"/>) run one at a time: contexts side by side
/// on one surfaceless EGL display have crashed the test host under load. (OffscreenGl itself also makes and lets go of
/// contexts one at a time and terminates the display only after the last.)
/// </summary>
[CollectionDefinition("OpenGL")]
public sealed class OpenGlCollection;
