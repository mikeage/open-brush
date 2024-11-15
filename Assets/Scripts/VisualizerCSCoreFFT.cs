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
#if !DISABLE_AUDIO_CAPTURE && !UNITY_OSX && !UNITY_EDITOR_OSX
using CSCore.DSP;
#endif

namespace TiltBrush
{
    /// Wrapper for CSCore.DSP.FftProvider
    public class VisualizerCSCoreFft : VisualizerManager.Fft
    {
#if !DISABLE_AUDIO_CAPTURE && !UNITY_OSX && !UNITY_EDITOR_OSX
        private FftProvider m_Fft;
        public VisualizerCSCoreFft(int channels, int fftSize)
        {
            FftSize size = (FftSize)fftSize;
            m_Fft = new FftProvider(channels, size);
        }

        public override void Add(float[] samples, int count)
        {
            m_Fft.Add(samples, count);
        }

        public override void GetFftData(float[] resultBuffer)
        {
            m_Fft.GetFftData(resultBuffer);
        }
#endif
    }
}
