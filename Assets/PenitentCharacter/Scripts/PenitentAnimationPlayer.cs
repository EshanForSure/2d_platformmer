using System;
using System.Collections.Generic;
using UnityEngine;

namespace PenitentCharacter
{
    [RequireComponent(typeof(SpriteRenderer))]
    public class PenitentAnimationPlayer : MonoBehaviour
    {
        private SpriteRenderer spriteRenderer;
        private PenitentAnimationCatalog catalog;

        public string CurrentAnimationName { get; private set; } = "";
        public int CurrentFrameIndex { get; private set; } = 0;
        public bool IsFinished { get; private set; } = false;

        private PenitentAnimationData currentAnim;
        private float frameTimer = 0f;

        public event Action<string> OnAnimationCompleted;
        public event Action<string, int> OnFrameChanged;

        private void Awake()
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
            catalog = GetComponentInParent<PenitentAnimationCatalog>();
            if (catalog == null)
            {
                catalog = FindFirstObjectByType<PenitentAnimationCatalog>();
            }
        }

        private void Update()
        {
            if (currentAnim == null || currentAnim.sprites == null || currentAnim.sprites.Count == 0)
                return;

            float frameDuration = 1f / Mathf.Max(1f, currentAnim.fps);
            frameTimer += Time.deltaTime;

            if (frameTimer >= frameDuration)
            {
                frameTimer -= frameDuration;
                AdvanceFrame();
            }
        }

        public void PlayAnimation(string animName, bool forceRestart = false)
        {
            if (CurrentAnimationName == animName && !forceRestart && !IsFinished)
                return;

            if (catalog == null)
            {
                catalog = GetComponentInParent<PenitentAnimationCatalog>();
                if (catalog == null) catalog = FindFirstObjectByType<PenitentAnimationCatalog>();
                if (catalog == null) return;
            }

            var anim = catalog.GetAnimation(animName);
            if (anim == null || anim.sprites.Count == 0)
                return;

            currentAnim = anim;
            CurrentAnimationName = animName;
            CurrentFrameIndex = 0;
            IsFinished = false;
            frameTimer = 0f;

            ApplyCurrentFrame();
            OnFrameChanged?.Invoke(CurrentAnimationName, CurrentFrameIndex);
        }

        private void AdvanceFrame()
        {
            if (currentAnim == null || currentAnim.sprites.Count == 0) return;

            if (CurrentFrameIndex + 1 < currentAnim.sprites.Count)
            {
                CurrentFrameIndex++;
                ApplyCurrentFrame();
                OnFrameChanged?.Invoke(CurrentAnimationName, CurrentFrameIndex);
            }
            else
            {
                if (currentAnim.loop)
                {
                    CurrentFrameIndex = 0;
                    ApplyCurrentFrame();
                    OnFrameChanged?.Invoke(CurrentAnimationName, CurrentFrameIndex);
                }
                else
                {
                    IsFinished = true;
                    OnAnimationCompleted?.Invoke(CurrentAnimationName);
                }
            }
        }

        private void ApplyCurrentFrame()
        {
            if (currentAnim != null && CurrentFrameIndex >= 0 && CurrentFrameIndex < currentAnim.sprites.Count)
            {
                spriteRenderer.sprite = currentAnim.sprites[CurrentFrameIndex];
            }
        }

        public void SetFacing(bool faceRight)
        {
            // The Penitent sprites natively face right.
            transform.localScale = new Vector3(faceRight ? 1f : -1f, 1f, 1f);
        }
    }
}
