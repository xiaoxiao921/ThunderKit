﻿using UnityEngine;

namespace ThunderKit.Core
{
    public class ComposableElement : ScriptableObject
    {
        [HideInInspector]
        public bool Errored;
        [HideInInspector]
        public string ErrorMessage;
        [HideInInspector]
        public string ErrorStacktrace;

        private void Awake()
        {
            name = GetType().Name;
        }
    }
}