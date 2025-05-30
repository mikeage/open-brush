﻿// Copyright 2020 The Tilt Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization.Settings;

namespace TiltBrush
{

    public class EnvironmentCatalog : MonoBehaviour
    {
        static public EnvironmentCatalog m_Instance;

        public event Action EnvironmentsChanged;
        public Material m_SkyboxMaterial;

        [SerializeField] private TiltBrush.Environment m_DefaultEnvironment;

        [SerializeField] private TiltBrush.Environment m_PassthroughEnvironment;

        private bool m_IsLoading;
        private Dictionary<Guid, Environment> m_GuidToEnvironment;

        // Used by the HTTP API because you can't GetLocalizedString in a background thread.
        public List<string> m_EnvironmentDescriptions;

        public IEnumerable<Environment> AllEnvironments
        {
            get { return m_GuidToEnvironment.Values; }
        }
        public Environment DefaultEnvironment
        {
            get
            {
#if ZAPBOX_SUPPORTED
                // Load into passthrough straight away.
                return m_PassthroughEnvironment;
#endif
                return m_DefaultEnvironment;

            }
        }

        void Awake()
        {
            m_Instance = this;
            m_GuidToEnvironment = new Dictionary<Guid, Environment>();
            m_EnvironmentDescriptions = new List<string>();
        }

        public bool IsLoading { get { return m_IsLoading; } }

        public void BeginReload()
        {
            var newEnvironments = new List<Environment>();
            LoadEnvironmentsInManifest(newEnvironments);
            newEnvironments.Add(DefaultEnvironment);

            m_GuidToEnvironment.Clear();
            foreach (var env in newEnvironments)
            {
                Environment tmp;
                if (m_GuidToEnvironment.TryGetValue(env.m_Guid, out tmp) && tmp != env)
                {
                    Debug.LogErrorFormat("Guid collision: {0}, {1}", tmp, env);
                    continue;
                }
                m_GuidToEnvironment[env.m_Guid] = env;
                // TODO - do we want the API to always use English?
                // env.m_EnvironmentDescription.LocaleOverride = LocalizationSettings.AvailableLocales.GetLocale(SystemLanguage.English);
                m_EnvironmentDescriptions.Add(env.Description);
                // env.m_EnvironmentDescription.LocaleOverride = null;
            }

            Resources.UnloadUnusedAssets();
            m_IsLoading = true;
        }

        public Environment GetEnvironment(Guid guid)
        {
            try
            {
                return m_GuidToEnvironment[guid];
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        void Update()
        {
            if (m_IsLoading)
            {
                m_IsLoading = false;
                Resources.UnloadUnusedAssets();
                if (EnvironmentsChanged != null)
                {
                    EnvironmentsChanged();
                }
            }
        }

        static void LoadEnvironmentsInManifest(List<Environment> output)
        {
            var manifest = App.Instance.ManifestFull;
            foreach (var asset in manifest.Environments)
            {
                if (asset != null)
                {
                    output.Add(asset);
                }
            }
        }
    }
} // namespace TiltBrush
