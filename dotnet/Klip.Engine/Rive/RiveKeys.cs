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

    // ================= MÁQUINAS DE ESTADOS =================
    // Extraído de external/rive-runtime/include/rive/generated/animation/*_base.hpp (typeKey / *PropertyKey).
    // ARMADILHA: os type keys e os property keys vivem em espaços numéricos DIFERENTES — 138 é o
    // type key do CubicValueInterpolator E o property key do "name" de um StateMachineComponent.
    // Não há conflito porque o parser só compara type keys com type keys.

    // ---- type keys (state machine) ----
    public const int StateMachine = 53;
    public const int StateMachineComponent = 54;          // abstracto (nunca aparece no ficheiro)
    public const int StateMachineInput = 55;              // abstracto
    public const int StateMachineNumber = 56, StateMachineTrigger = 58, StateMachineBool = 59;
    public const int StateMachineLayer = 57;
    public const int LayerState = 60;                     // estado "vazio" genérico
    public const int AnimationState = 61, AnyState = 62, EntryState = 63, ExitState = 64;
    public const int StateMachineLayerComponent = 66;     // abstracto
    public const int AdvanceableState = 145;              // abstracto
    public const int StateTransition = 65, BlendStateTransition = 78;
    public const int TransitionInputCondition = 67;       // abstracto
    public const int TransitionTriggerCondition = 68;
    public const int TransitionValueCondition = 69;       // abstracto
    public const int TransitionNumberCondition = 70, TransitionBoolCondition = 71;
    // Lista COMPLETA dos descendentes de LayerState (grep "public LayerState/BlendState" no runtime).
    // Falhar um deles é caro: o estado não entra na lista da camada e TODOS os índices de destino
    // das transições a partir daí passam a apontar para o sítio errado.
    public const int BlendState = 72, BlendStateDirect = 73, BlendState1DInput = 76;
    public const int BlendState1D = 527, BlendState1DViewModel = 528;
    public const int BlendAnimation = 74;                 // abstracto
    public const int BlendAnimation1D = 75, BlendAnimationDirect = 77;
    public const int StateMachineListener = 114;          // escutas de rato (antigo) — ignoradas por agora
    public const int StateMachineListenerNovo = 654;      // escutas de rato (formato novo)
    public const int ListenerTriggerChange = 115, ListenerInputChange = 116;
    public const int ListenerBoolChange = 117, ListenerNumberChange = 118;

    // ---- property keys (state machine) ----
    // ARMADILHA de herança: StateMachineBase deriva de Animation (não de StateMachineComponent),
    // por isso o nome da MÁQUINA usa a chave 55 — a mesma da LinearAnimation. Só os inputs e as
    // camadas é que usam a 138. Os estados (LayerState → StateMachineLayerComponent → Core) NÃO
    // têm sequer propriedade de nome: o editor não a exporta, o nome é só do editor.
    public const int SmNameKey = 138;                     // StateMachineComponent::name (input, camada)
    public const int SmMachineNameKey = AnimNameKey;      // = 55, Animation::name (a máquina)
    public const int SmNumberValueKey = 140;              // StateMachineNumber::value   (double)
    public const int SmBoolValueKey = 141;                // StateMachineBool::value     (bool)
    public const int AnimationStateAnimIdKey = 149;       // índice na lista de animações do artboard
    public const int TrStateToIdKey = 151;                // índice na lista de estados da MESMA camada
    public const int TrFlagsKey = 152;                    // StateTransitionFlags
    public const int TrInputIdKey = 155;                  // índice na lista de inputs da máquina
    public const int TrOpValueKey = 156;                  // TransitionConditionOp
    public const int TrNumberValueKey = 157;              // valor a comparar (double)
    public const int TrDurationKey = 158;                 // ms ou % conforme a flag
    public const int TrExitTimeKey = 160;                 // ms ou % conforme a flag
    public const int TrInterpTypeKey = 349, TrInterpolatorIdKey = 350;
    public const int TrRandomWeightKey = 537;
    public const int LayerStateFlagsKey = 536;            // bit0 = escolha aleatória de transição
    public const int BlendAnimIdKey = 165, BlendAnim1DValueKey = 166;
    public const int BlendState1DInputIdKey = 167, BlendAnimDirectInputIdKey = 168;
    public const int AdvanceableSpeedKey = 292;
}
