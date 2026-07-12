namespace Klip.Engine.Rive;

/// <summary>Type keys + property keys extracted from the rive-runtime C++ generated headers.</summary>
public static class RiveKeys
{
    // ---- type keys ----
    public const int Backboard = 23, Artboard = 1;
    public const int Node = 2, Shape = 3, Drawable = 13;
    public const int Path = 12, PointsPath = 16, PointsCommonPath = 620, ParametricPath = 15;
    public const int Rectangle = 7, Ellipse = 4, Triangle = 8;
    public const int PathVertex = 14, StraightVertex = 5;
    public const int CubicVertex = 36, CubicDetachedVertex = 6, CubicMirroredVertex = 35, CubicAsymmetricVertex = 34;
    public const int Fill = 20, Stroke = 24, SolidColor = 18;
    public const int LinearGradient = 22, RadialGradient = 17, GradientStop = 19, TrimPath = 47;
    public const int LinearAnimation = 31, KeyedObject = 25, KeyedProperty = 26;
    public const int KeyFrameDouble = 30, KeyFrameColor = 37, KeyFrameId = 50;
    public const int CubicInterpolator = 139, CubicEaseInterpolator = 28, CubicValueInterpolator = 138;

    // ---- property keys ----
    // Component
    public const int NameKey = 4, ParentIdKey = 5;
    // WorldTransformComponent / TransformComponent / Node
    public const int OpacityKey = 18, RotationKey = 15, ScaleXKey = 16, ScaleYKey = 17, XKey = 13, YKey = 14;
    // Drawable
    public const int BlendModeKey = 23, DrawableFlagsKey = 129;
    // Artboard (LayoutComponent)
    public const int ArtboardWidthKey = 7, ArtboardHeightKey = 8, ArtboardOriginXKey = 11, ArtboardOriginYKey = 12;
    // ParametricPath
    public const int PpWidthKey = 20, PpHeightKey = 21, PpOriginXKey = 123, PpOriginYKey = 124;
    // Rectangle corners
    public const int CornerTL = 31, CornerTR = 161, CornerBL = 162, CornerBR = 163, LinkCorner = 164;
    // PointsCommonPath
    public const int IsClosedKey = 32;
    // Vertex
    public const int VertexXKey = 24, VertexYKey = 25, StraightRadiusKey = 26;
    // CubicDetachedVertex
    public const int InRotationKey = 84, InDistanceKey = 85, OutRotationKey = 86, OutDistanceKey = 87;
    // CubicMirroredVertex
    public const int MirrorRotationKey = 82, MirrorDistanceKey = 83;
    // CubicAsymmetricVertex
    public const int AsymRotationKey = 79, AsymInDistanceKey = 80, AsymOutDistanceKey = 81;
    // ShapePaint / Fill / Stroke
    public const int IsVisibleKey = 41, FillRuleKey = 40;
    public const int ThicknessKey = 47, CapKey = 48, JoinKey = 49;
    // SolidColor / GradientStop
    public const int SolidColorKey = 37, StopColorKey = 38, StopPositionKey = 39;
    // Gradient
    public const int GradStartXKey = 42, GradStartYKey = 33, GradEndXKey = 34, GradEndYKey = 35, GradOpacityKey = 46;
    // TrimPath
    public const int TrimStartKey = 114, TrimEndKey = 115, TrimOffsetKey = 116, TrimModeKey = 117;
    // LinearAnimation
    public const int FpsKey = 56, DurationKey = 57, SpeedKey = 58, LoopValueKey = 59, AnimNameKey = 55;
    // Keyed
    public const int ObjectIdKey = 51, KeyedPropertyKey = 53;
    // KeyFrame
    public const int FrameKey = 67, InterpTypeKey = 68, InterpolatorIdKey = 69;
    public const int KfDoubleValueKey = 70, KfColorValueKey = 88, KfIdValueKey = 122;
    // CubicInterpolator
    public const int CiX1 = 63, CiY1 = 64, CiX2 = 65, CiY2 = 66;
}
