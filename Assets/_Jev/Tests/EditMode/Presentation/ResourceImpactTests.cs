using System.Collections;
using System.Reflection;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class ResourceImpactTests
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        GameObject root, visual;
        InteractionFeedback feedback;

        [SetUp] public void SetUp()
        {
            root = new GameObject("resource feedback test");
            visual = new GameObject("authored resource");
            visual.transform.rotation = Quaternion.Euler(0, 37, 0);
            feedback = root.AddComponent<InteractionFeedback>();
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(root); Object.DestroyImmediate(visual); }
        void Update() => typeof(InteractionFeedback).GetMethod("Update", Private).Invoke(feedback, null);

        [Test] public void FinalMiningImpactRetainsMineUntilContactThenAllowsDepletedVisualToHide()
        {
            feedback.Queue(InteractionImpact.Mining, Vector3.zero, Vector3.forward, new Cell(3, 3), visual.transform, 0, true);
            Assert.That(feedback.RetainResource(visual.transform), Is.True);
            Update();
            Assert.That(feedback.PlayedImpacts, Is.EqualTo(1));
            Assert.That(feedback.RetainResource(visual.transform), Is.False);
        }

        [Test] public void ExhaustedTreeFallsAfterContactAndRestoresAuthoredTransformForNextMatch()
        {
            var rest = visual.transform.localRotation;
            feedback.Queue(InteractionImpact.Wood, Vector3.zero, Vector3.forward, new Cell(3, 3), visual.transform, 0, true);
            Update();
            Assert.That(feedback.RetainResource(visual.transform), Is.True);
            var recoils = (IList)typeof(InteractionFeedback).GetField("recoils", Private).GetValue(feedback);
            object fall = recoils[0];
            fall.GetType().GetField("Start").SetValue(fall, Time.time - feedback.TreeFallDuration * .5f);
            Update();
            Assert.That(Quaternion.Angle(visual.transform.localRotation, rest), Is.GreaterThan(15));
            Assert.That(visual.activeSelf, Is.True);
            fall.GetType().GetField("Start").SetValue(fall, Time.time - feedback.TreeFallDuration - .1f);
            Update();
            Assert.That(visual.activeSelf, Is.False);
            Assert.That(feedback.RetainResource(visual.transform), Is.False);
            Assert.That(Quaternion.Angle(visual.transform.localRotation, rest), Is.LessThan(.01));
        }

        [Test] public void ResetDuringFallRestoresTreeWithoutChangingResourceEconomy()
        {
            var rest = visual.transform.localRotation;
            feedback.Queue(InteractionImpact.Wood, Vector3.zero, Vector3.forward, new Cell(3, 3), visual.transform, 0, true);
            Update();
            feedback.Clear();
            Assert.That(feedback.RetainResource(visual.transform), Is.False);
            Assert.That(visual.activeSelf, Is.True);
            Assert.That(Quaternion.Angle(visual.transform.localRotation, rest), Is.LessThan(.01));
        }
    }
}
