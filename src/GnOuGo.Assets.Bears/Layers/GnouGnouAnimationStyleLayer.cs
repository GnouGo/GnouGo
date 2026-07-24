namespace GnOuGo.Assets.Bears.Layers;

internal static class GnouGnouAnimationStyleLayer
{
    public static string Render(GnouGnouBearAnimation animation)
    {
        if (animation == GnouGnouBearAnimation.None)
            return string.Empty;

        var token = GnouGnouBearAnimationNames.ToToken(animation);
        var rules = animation switch
        {
            GnouGnouBearAnimation.Idle => IdleRules,
            GnouGnouBearAnimation.Walk => WalkRules,
            GnouGnouBearAnimation.Typing => TypingRules,
            GnouGnouBearAnimation.Waiting => WaitingRules,
            GnouGnouBearAnimation.Pickup => GestureRules("pickup", "-36deg", "36deg", "7px"),
            GnouGnouBearAnimation.Handoff => GestureRules("handoff", "-44deg", "44deg", "-2px"),
            GnouGnouBearAnimation.Delivery => GestureRules("delivery", "86deg", "-86deg", "-8px"),
            GnouGnouBearAnimation.Clone => GestureRules("clone", "70deg", "-70deg", "-6px"),
            GnouGnouBearAnimation.Merge => GestureRules("merge", "-42deg", "42deg", "0px"),
            GnouGnouBearAnimation.Celebration => CelebrationRules,
            GnouGnouBearAnimation.Failure => FailureRules,
            _ => throw new ArgumentOutOfRangeException(nameof(animation), animation, "Unsupported GnOuGo animation.")
        };
        rules = rules.Replace(
            ".gnougo-rig[data-animation] ",
            $".gnougo-rig[data-animation=\"{token}\"] ",
            StringComparison.Ordinal);

        return $$"""
  <style>
    .gnougo-rig[data-animation="{{token}}"] [data-part] { transform-box: view-box; }
    .gnougo-rig [data-part="body"] { transform-origin: 128px 181px; }
    .gnougo-rig [data-part="head"] { transform-origin: 128px 151px; }
    .gnougo-rig [data-part="ear-left"] { transform-origin: 91px 82px; }
    .gnougo-rig [data-part="ear-right"] { transform-origin: 165px 82px; }
    .gnougo-rig [data-part="arm-left"] { transform-origin: 94px 157px; }
    .gnougo-rig [data-part="arm-right"] { transform-origin: 162px 157px; }
    .gnougo-rig [data-part="leg-left"] { transform-origin: 104px 179px; }
    .gnougo-rig [data-part="leg-right"] { transform-origin: 152px 179px; }
    .gnougo-rig [data-part="eye-left"], .gnougo-rig [data-part="eye-right"] { transform-origin: center; }
    .gnougo-rig [data-part="brow-left"] { transform-origin: 104px 83px; }
    .gnougo-rig [data-part="brow-right"] { transform-origin: 152px 83px; }
    .gnougo-rig [data-part="mouth"] { transform-origin: 128px 145px; }
{{rules}}
    @media (prefers-reduced-motion: reduce) {
      .gnougo-rig[data-animation="{{token}}"] [data-part] { animation: none !important; }
    }
  </style>
""";
    }

    private const string LivingFaceRules = """
    .gnougo-rig[data-animation] [data-part="body"] { animation: gnougo-breathe 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation] [data-part="eye-left"],
    .gnougo-rig[data-animation] [data-part="eye-right"] { animation: gnougo-blink 7.4s linear infinite; }
    .gnougo-rig[data-animation] [data-part="pupil-left"],
    .gnougo-rig[data-animation] [data-part="pupil-right"] { animation: gnougo-pupil-look 15.4s ease-in-out infinite; }
    .gnougo-rig[data-animation] [data-part="ear-left"] { animation: gnougo-ear-left 11s ease-in-out infinite; }
    .gnougo-rig[data-animation] [data-part="ear-right"] { animation: gnougo-ear-right 13s ease-in-out .8s infinite; }
    .gnougo-rig[data-animation] [data-part="mouth"] { animation: gnougo-mouth-life 58s ease-in-out infinite; }
    @keyframes gnougo-breathe { 0%, 100% { transform: translateY(0) scaleY(1); } 50% { transform: translateY(-.45px) scaleY(1.004); } }
    @keyframes gnougo-blink { 0%, 46%, 49%, 100% { transform: scaleY(1); } 47.4% { transform: scaleY(.08); } }
    @keyframes gnougo-pupil-look { 0%, 100% { transform: translate(0 0); } 38% { transform: translate(1.2px -.25px); } 71% { transform: translate(-1px .2px); } }
    @keyframes gnougo-ear-left { 0%, 67%, 76%, 100% { transform: rotate(0); } 71% { transform: rotate(7deg); } }
    @keyframes gnougo-ear-right { 0%, 43%, 52%, 100% { transform: rotate(0); } 47% { transform: rotate(-6deg); } }
    @keyframes gnougo-mouth-life {
      0%, 100% { transform: scale(1.04,1.08); }
      22%, 58% { transform: rotate(.45deg) scale(1.05,1.1); }
      88%, 97% { transform: scale(1.04,1.08); }
      92% { transform: translateY(1.5px) scale(1.06,1.62); }
      94% { transform: translateY(2.2px) scale(1.08,2.05); }
    }
""";

    private const string IdleRules = LivingFaceRules + """
    .gnougo-rig[data-animation="idle"] [data-part="head"] { animation: gnougo-idle-head 13s ease-in-out infinite; }
    .gnougo-rig[data-animation="idle"] [data-part="arm-right"] { animation: gnougo-yawn-arm 58s ease-in-out infinite; }
    @keyframes gnougo-idle-head { 0%, 100% { transform: rotate(-.6deg); } 50% { transform: rotate(.6deg); } }
    @keyframes gnougo-yawn-arm { 0%, 88%, 100% { transform: rotate(0); } 92%, 96% { transform: rotate(-58deg); } }
""";

    private const string WalkRules = LivingFaceRules + """
    .gnougo-rig[data-animation="walk"] [data-part="leg-left"] { animation: gnougo-walk-left 1.65s ease-in-out infinite; }
    .gnougo-rig[data-animation="walk"] [data-part="leg-right"] { animation: gnougo-walk-right 1.65s ease-in-out infinite; }
    .gnougo-rig[data-animation="walk"] [data-part="arm-left"] { animation: gnougo-walk-arm-left 1.65s ease-in-out infinite; }
    .gnougo-rig[data-animation="walk"] [data-part="arm-right"] { animation: gnougo-walk-arm-right 1.65s ease-in-out infinite; }
    .gnougo-rig[data-animation="walk"] [data-part="head"] { animation: gnougo-walk-head 3.3s ease-in-out infinite; }
    @keyframes gnougo-walk-left {
      0%, 46%, 100% { transform: rotate(0) translateY(0); }
      20% { transform: rotate(28deg) translateY(-3px); }
      38% { transform: rotate(-5deg) translateY(0); }
    }
    @keyframes gnougo-walk-right {
      0%, 50%, 100% { transform: rotate(0) translateY(0); }
      70% { transform: rotate(-28deg) translateY(-3px); }
      88% { transform: rotate(5deg) translateY(0); }
    }
    @keyframes gnougo-walk-arm-left {
      0%, 50%, 100% { transform: rotate(0); }
      70% { transform: rotate(18deg); }
      88% { transform: rotate(-5deg); }
    }
    @keyframes gnougo-walk-arm-right {
      0%, 46%, 100% { transform: rotate(0); }
      20% { transform: rotate(-18deg); }
      38% { transform: rotate(5deg); }
    }
    @keyframes gnougo-walk-head { 0%,100% { transform: rotate(-.55deg) translateY(0); } 50% { transform: rotate(.55deg) translateY(-.55px); } }
""";

    private const string TypingRules = LivingFaceRules + """
    .gnougo-rig[data-animation="typing"] [data-part="arm-left"] { animation: gnougo-type-left 1.05s ease-in-out infinite; }
    .gnougo-rig[data-animation="typing"] [data-part="arm-right"] { animation: gnougo-type-right 1.05s ease-in-out infinite; }
    .gnougo-rig[data-animation="typing"] [data-part="head"] { animation: gnougo-type-head 4.8s ease-in-out infinite; }
    @keyframes gnougo-type-left { 0%,100% { transform: rotate(-31deg); } 50% { transform: rotate(-37deg) translateY(1px); } }
    @keyframes gnougo-type-right { 0%,100% { transform: rotate(31deg); } 50% { transform: rotate(37deg) translateY(1px); } }
    @keyframes gnougo-type-head { 0%,100% { transform: translateY(3px) rotate(-.7deg); } 50% { transform: translateY(4px) rotate(.7deg); } }
""";

    private const string WaitingRules = LivingFaceRules + """
    .gnougo-rig[data-animation="waiting"] [data-part="body"] { animation: gnougo-wait-body 8s ease-in-out infinite; }
    .gnougo-rig[data-animation="waiting"] [data-part="head"] { animation: gnougo-wait-head 12s ease-in-out infinite; }
    .gnougo-rig[data-animation="waiting"] [data-part="arm-right"] { animation: gnougo-yawn-arm 58s ease-in-out infinite; }
    @keyframes gnougo-wait-body { 0%,100% { transform: translateY(0); } 50% { transform: translateY(-.5px) scaleY(1.003); } }
    @keyframes gnougo-wait-head { 0%,100% { transform: rotate(-2deg); } 50% { transform: rotate(2deg); } }
    @keyframes gnougo-yawn-arm { 0%, 88%, 100% { transform: rotate(0); } 92%, 96% { transform: rotate(-58deg); } }
""";

    private const string CelebrationRules = LivingFaceRules + """
    .gnougo-rig[data-animation="celebration"] [data-part="arm-left"] { animation: gnougo-celebrate-left 2.8s ease-in-out infinite; }
    .gnougo-rig[data-animation="celebration"] [data-part="arm-right"] { animation: gnougo-celebrate-right 2.8s ease-in-out infinite; }
    .gnougo-rig[data-animation="celebration"] [data-part="body"] { animation: gnougo-celebrate-body 2.8s ease-in-out infinite; }
    @keyframes gnougo-celebrate-left { 0%,100% { transform: rotate(0); } 45%,65% { transform: rotate(88deg); } }
    @keyframes gnougo-celebrate-right { 0%,100% { transform: rotate(0); } 45%,65% { transform: rotate(-88deg); } }
    @keyframes gnougo-celebrate-body { 0%,100% { transform: translateY(0); } 52% { transform: translateY(-5px); } }
""";

    private const string FailureRules = LivingFaceRules + """
    .gnougo-rig[data-animation="failure"] [data-part="body"] { animation: gnougo-fail-body 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-part="head"] { animation: gnougo-fail-head 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-part="ear-left"] { animation: gnougo-fail-ear-left 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-part="ear-right"] { animation: gnougo-fail-ear-right 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-part="eye-left"],
    .gnougo-rig[data-animation="failure"] [data-part="eye-right"] { animation: gnougo-fail-eye 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-part="pupil-left"],
    .gnougo-rig[data-animation="failure"] [data-part="pupil-right"] { animation: gnougo-fail-pupil 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-part="brow-left"] { transform: rotate(10deg); }
    .gnougo-rig[data-animation="failure"] [data-part="brow-right"] { transform: rotate(-10deg); }
    .gnougo-rig[data-animation="failure"] [data-part="mouth"] { animation: gnougo-fail-mouth 7.2s ease-in-out infinite; }
    .gnougo-rig[data-animation="failure"] [data-expression="default"] { opacity: 0; }
    .gnougo-rig[data-animation="failure"] [data-expression="failure"] { opacity: 1; }
    @keyframes gnougo-fail-body { 0%,100% { transform: translateY(7px) scaleY(.95); } 50% { transform: translateY(6.7px) scaleY(.952); } }
    @keyframes gnougo-fail-head { 0%,100% { transform: translateY(11px) rotate(12deg); } 50% { transform: translateY(10.7px) rotate(11.7deg); } }
    @keyframes gnougo-fail-ear-left { 0%,100% { transform: rotate(-16deg); } 50% { transform: rotate(-14.5deg); } }
    @keyframes gnougo-fail-ear-right { 0%,100% { transform: rotate(16deg); } 50% { transform: rotate(14.5deg); } }
    @keyframes gnougo-fail-eye { 0%,46%,50%,100% { transform: scaleY(.72); } 48% { transform: scaleY(.08); } }
    @keyframes gnougo-fail-pupil { 0%,100% { transform: translate(0 3px); } 50% { transform: translate(.35px 3.2px); } }
    @keyframes gnougo-fail-mouth { 0%,100% { transform: translateY(2px) scale(.98,.92); } 50% { transform: translateY(2.2px) scale(1,.94); } }
""";

    private static string GestureRules(string name, string leftAngle, string rightAngle, string bodyY) => LivingFaceRules + $$"""
    .gnougo-rig[data-animation="{{name}}"] [data-part="arm-left"] { animation: gnougo-{{name}}-left 3.4s ease-in-out infinite; }
    .gnougo-rig[data-animation="{{name}}"] [data-part="arm-right"] { animation: gnougo-{{name}}-right 3.4s ease-in-out infinite; }
    .gnougo-rig[data-animation="{{name}}"] [data-part="body"] { animation: gnougo-{{name}}-body 3.4s ease-in-out infinite; }
    @keyframes gnougo-{{name}}-left { 0%,100% { transform: rotate(0); } 42%,64% { transform: rotate({{leftAngle}}); } }
    @keyframes gnougo-{{name}}-right { 0%,100% { transform: rotate(0); } 42%,64% { transform: rotate({{rightAngle}}); } }
    @keyframes gnougo-{{name}}-body { 0%,100% { transform: translateY(0); } 42%,64% { transform: translateY({{bodyY}}); } }
""";
}
