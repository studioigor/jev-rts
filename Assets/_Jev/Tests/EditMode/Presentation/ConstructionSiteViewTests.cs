using System.Reflection;
using Jev.Gameplay.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class ConstructionSiteViewTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject root;
        ConstructionSiteView view;
        RectTransform card;
        UnityEngine.UI.Image fill;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Foundation"); view = root.AddComponent<ConstructionSiteView>();
            var pivot = new GameObject("Compensator").transform; pivot.SetParent(root.transform, false);
            card = new GameObject("Card", typeof(RectTransform)).GetComponent<RectTransform>();
            card.SetParent(pivot, false); card.sizeDelta = new Vector2(160, 40);
            fill = new GameObject("Fill", typeof(RectTransform), typeof(UnityEngine.UI.Image)).GetComponent<UnityEngine.UI.Image>();
            fill.transform.SetParent(card, false);
            Set("scaleCompensator", pivot); Set("progressCanvas", card); Set("progressFill", fill);
        }
        [TearDown] public void TearDown() => Object.DestroyImmediate(root);
        void Set(string name, object value) => typeof(ConstructionSiteView).GetField(name, Private).SetValue(view, value);

        [Test]
        public void AuthoredBarShowsRealWorkAndResetsForPlannedSite()
        {
            view.SetProgress(1, 4, false);
            Assert.That(fill.rectTransform.anchorMax.x, Is.EqualTo(.25f));
            view.SetProgress(9, 4, false);
            Assert.That(fill.rectTransform.anchorMax.x, Is.EqualTo(1));
            view.SetProgress(9, 4, true);
            Assert.That(fill.rectTransform.anchorMax.x, Is.Zero);
            view.SetProgress(1, 0, false);
            Assert.That(fill.rectTransform.anchorMax.x, Is.Zero);
        }

        [Test]
        public void BillboardRemainsSquareUnderNonuniformFoundationScale()
        {
            root.transform.localScale = new Vector3(3, 1, 5);
            root.transform.rotation = Quaternion.Euler(0, 34, 0);
            var cameraObject = new GameObject("Construction view camera");
            var camera = cameraObject.AddComponent<UnityEngine.Camera>();
            var target = new RenderTexture(1000, 1000, 0);
            try
            {
                camera.targetTexture = target;
                camera.transform.position = new Vector3(-10, 15, -20); camera.transform.LookAt(Vector3.zero);
                Set("viewCamera", camera);
                typeof(ConstructionSiteView).GetMethod("LateUpdate", Private).Invoke(view, null);
                Vector3 x = card.localToWorldMatrix.MultiplyVector(Vector3.right), y = card.localToWorldMatrix.MultiplyVector(Vector3.up);
                Assert.That(x.magnitude, Is.EqualTo(y.magnitude).Within(.0001f), "A stretched foundation must not stretch the progress text.");
                Assert.That(Vector3.Dot(x.normalized, y.normalized), Is.EqualTo(0).Within(.0001f), "The card must not inherit shear.");
                Assert.That(Quaternion.Angle(card.rotation, camera.transform.rotation), Is.LessThan(.01f));
                Assert.That(card.position.y, Is.EqualTo(1.15f).Within(.0001f));
            }
            finally
            {
                camera.targetTexture = null; Object.DestroyImmediate(target); Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
