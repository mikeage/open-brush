﻿// Copyright 2024 The Open Brush Authors
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
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class StringPickerItemButton : BaseButton
    {
        private int m_ItemIndex;
        public Action<int> m_OnItemSelected;
        private string m_ButtonLabel;
        public string ButtonLabel
        {
            get { return m_ButtonLabel; }
            set
            {
                GetComponentInChildren<TextMeshPro>().text = value;
                m_ButtonLabel = value;
            }
        }

        public void SetPreset(Texture2D tex, string itemName, int itemIndex)
        {
            m_ItemIndex = itemIndex;
            ButtonLabel = itemName;
            SetDescriptionText(itemName);
        }

        override protected void OnButtonPressed()
        {
            m_OnItemSelected.Invoke(m_ItemIndex);
        }
    }
} // namespace TiltBrush
