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

namespace TiltBrush
{
    public interface IReferenceItemCatalog
    {
        int ItemCount { get; }
        bool IsScanning { get; }
        event Action CatalogChanged;
        void ForceCatalogScan();
        void ChangeDirectory(string path);
        bool IsHomeDirectory();
        string HomeDirectory { get; }
        bool IsSubDirectoryOfHome();
        public string GetCurrentDirectory();
    }
} // namespace TiltBrush
