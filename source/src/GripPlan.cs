using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>Where the hand goes and how it is shaped, stored against a weapon part so it follows the gun.</summary>
    internal sealed class GripPlan
    {
        internal Transform Anchor;
        internal Vector3 RootPos;            // hand-copy root (HandPoser bone) in Anchor space
        internal Quaternion RootRot;
        internal Quaternion[] Fingers;       // hand-copy local rotations
        internal Vector3 CentreLocal, AxisLocal;
        internal float TwistMin, TwistMax;   // knob: how far the hand can follow the knob without hitting anything
        internal bool FromPose;              // player's saved / edited pose: knob turns in strokes within TwistMin..TwistMax
        internal bool LeverPush;             // lever: the hand swings forward / back about the lever instead of twisting
        internal float LeverAngle;           // lever: wrist turn that stays clear
        internal Vector3 PushAxisLocal, PushPivotLocal; // lever push: the lever's own pivot (Anchor space), + = towards its other position
        internal bool PushFromLever;         // the push pivot was measured from the two lever positions (else a guess)
        internal Vector3 PressDirLocal;      // button: the way the finger presses (Anchor space, down onto the scope)
        internal float ButtonTwist;          // button: 0 = press; else the hand twists this many degrees (like the Vudu knob)
        internal Vector3 TwistAxisLocal, TwistPivotLocal; // button twist: the line it turns about (Anchor space)
        internal bool Twists => Mathf.Abs(ButtonTwist) > 0.5f;
        internal Vector3 ApproachLocal;      // offset (Anchor space) of the in-between point the hand comes in from
        internal Hinge Flip;                 // flip magnifier swing (Part -> EndPart)
        internal float FlipFollow = 1f;      // how much of the flip the hand rides along (1 = all, 0.2 = a thumb flick)
        internal bool CloseFlip;             // closing pose: made on the OPEN part, the hand rides the first FlipFollow of the way back
        internal FlipMover FlipMover;        // what rides the flip: whole hand, thumb only (flicks a cover open), index only (beckons it shut)

        internal Vector3 Centre => Anchor.TransformPoint(CentreLocal);
        internal Vector3 Axis => Anchor.TransformDirection(AxisLocal).normalized;
    }
}
