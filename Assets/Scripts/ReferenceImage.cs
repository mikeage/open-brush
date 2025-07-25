// Copyright 2020 The Tilt Brush Authors
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

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.VectorGraphics;
using Superla.RadianceHDR;
using UnityEngine;
using UnityEngine.Localization;

namespace TiltBrush
{

    public class ReferenceImage
    {
        private enum ImageState
        {
            Uninitialized,
            // If m_coroutine == null, same meaning as Future.State.Start
            // If m_coroutine != null, same meaning as Future.State.Running
            NotReady,
            // Same meaning as Future.State.Done
            // Invariant: m_coroutine == null
            Ready,
            // Same meaning as Future.State.Error
            // Invariant: m_coroutine == null
            Error,
            // This is the only specific error message right now. ("Image too large to load")
            // For other errors (e.g unknown format), we set Error state and display a generic error message "Image failed to load"
            // ImageUtils.cs throws more specific errors, and we could implement them here as well in the future.
            ErrorImageTooLarge = 31000
        }

        // See ImageState for invariants
        private IEnumerator<Timeslice> m_coroutine;
        private ImageState m_State;
        private Texture2D m_Icon;
        private Texture2D m_FullSize;
        private int m_FullSizeReferences = 0;
        private float m_ImageAspect; // only valid if ImageState == Ready
        private string m_Path;
        private SVGParser.SceneInfo _SvgSceneInfo;

        private LocalizedString m_ErrorImageTooLargeHelpText = new LocalizedString("Strings", "PANEL_REFERENCE_ICONIMAGE_LOADERRORTEXT");
        private LocalizedString m_ErrorGenericHelpText = new LocalizedString("Strings", "PANEL_REFERENCE_ICONIMAGE_GENERICERRORTEXT");


        // public bool IsComposite => _SvgSceneInfo.Scene.Root.getsh

        public string FileName { get { return Path.GetFileName(m_Path); } }
        public string FileFullPath { get { return m_Path; } }

        // Aspect ratio of Icon (and of the fullres image, if applicable)
        public float ImageAspect
        {
            get
            {
                if (m_State == ImageState.Ready)
                {
                    return m_ImageAspect;
                }
                else
                {
                    // In case someone asks for the aspect ratio of the error icon
                    return 1;
                }
            }
        }

        // returns null if no error in image
        public string ImageErrorExtraDescription()
        {
            if (m_State != ImageState.Error && m_State != ImageState.ErrorImageTooLarge)
            {
                return null;
            }
            else if (m_State == ImageState.Error)
            {
                return m_ErrorGenericHelpText.GetLocalizedStringAsync().Result;
            }
            else
            {
                return m_ErrorImageTooLargeHelpText.GetLocalizedStringAsync().Result;
            }
        }

        public bool NotLoaded
        {
            get { return m_State == ImageState.Uninitialized || m_State == ImageState.NotReady; }
        }

        // true if IconSizedImage is neither null nor an error image.
        // Indicates that the image file can at least be loaded successfully
        public bool Valid { get { return m_State == ImageState.Ready; } }

        // Same meaning as Future.State.Running
        public bool Running { get { return m_coroutine != null; } }

        // An icon-sized version of the image, or an error icon if the file is unloadable.
        // Often resampled into a square, so use ImageAspect instead of calculating the
        // aspect ratio from this texture's size.
        public Texture2D Icon
        {
            get
            {
                switch (m_State)
                {
                    case ImageState.Ready: return m_Icon;
                    case ImageState.Error:
                    case ImageState.ErrorImageTooLarge: return ReferenceImageCatalog.m_Instance.ErrorImage;
                    default:
                    case ImageState.Uninitialized:
                    case ImageState.NotReady: return null;
                }
            }
        }

        // FullSize will always return *something*, even if it's actually the Icon texture.
        public Texture2D FullSize
        {
            get { return m_FullSize != null ? m_FullSize : m_Icon; }
        }

        /// You should probably use FileName instead.
        /// This property is only for those who need to load the image data from disk.
        public string FilePath { get { return m_Path; } }

        // Path relative to Catalog's HomeDirectory with forward slashes.
        public string RelativePath =>
            $".{FileFullPath.Substring(ReferenceImageCatalog.m_Instance.HomeDirectory.Length)}".Replace("\\", "/");

        public ReferenceImage(string path)
        {
            m_Path = path;
        }

        /// Returns a full-resolution Texture2D.
        /// The lifetime of this texture is handled by ReferenceImage, so should not be destroyed
        /// by the user of this method.
        public void AcquireImageFullsize(bool runForeground = false)
        {
            if (FilePath.EndsWith(".svg"))
            {
                // Try the cache first.
                m_FullSize = ImageCache.LoadImageCache(FilePath);
                if (m_FullSize == null)
                {
                    // TODO Move into the async code path?
                    var importer = new RuntimeSVGImporter();
                    _SvgSceneInfo = importer.ParseToSceneInfo(File.ReadAllText(FilePath));
                    m_FullSize = importer.ImportAsTexture(FilePath);
                    ImageCache.SaveImageCache(m_FullSize, FilePath);
                }
            }
            else
            {
                m_FullSizeReferences++;
                if (m_FullSizeReferences == 1)
                {
                    // Try the cache first.
                    m_FullSize = ImageCache.LoadImageCache(FilePath);
                    if (m_FullSize == null)
                    {
                        // Otherwise, this will generate a cache.
                        m_FullSize = Object.Instantiate(Icon);
                        var co = LoadImage(FilePath, m_FullSize, runForeground).GetEnumerator();
                        App.Instance.StartCoroutine(co);
                    }
                }
            }
        }

        /// Should be called when the texture from a reference image is no longer required.
        /// May destroy the full image texture.
        public void ReleaseImageFullsize()
        {
            if (m_FullSize != null && --m_FullSizeReferences == 0)
            {
                UnityEngine.Object.Destroy(m_FullSize);
                m_FullSize = null;
            }
        }

        // Helper for GetImageFullsize
        IEnumerable LoadImage(string path, Texture2D dest, bool runForeground = false)
        {
            // Temporarily increase the reference count during loading to prevent texture destruction if
            // ReturnImageFullSize is called during load.
            m_FullSizeReferences++;
            var reader = new ThreadedImageReader(path, -1,
                App.PlatformConfig.ReferenceImagesMaxDimension);
            while (!reader.Finished)
            {
                if (!runForeground) { yield return null; }
            }

            RawImage result = null;
            try
            {
                result = reader.Result;
                if (result != null && dest != null)
                {
                    int resizeLimit = App.PlatformConfig.ReferenceImagesResizeDimension;
                    if (result.ColorWidth > resizeLimit || result.ColorHeight > resizeLimit)
                    {
                        // Resize the image to the resize limit before saving it to the dest texture.
                        var tempTexture = new Texture2D(
                            result.ColorWidth, result.ColorHeight, TextureFormat.RGBA32, true);
                        tempTexture.SetPixels32(result.ColorData);
                        tempTexture.Apply();
                        DownsizeTexture(tempTexture, ref dest, resizeLimit);
                        Object.Destroy(tempTexture);
                    }
                    else
                    {
                        // Save the the image to the dest texture.
                        dest.Reinitialize(result.ColorWidth, result.ColorHeight, TextureFormat.RGBA32, true);
                        dest.SetPixels32(result.ColorData);
                        dest.Apply();
                    }

                    // Cache the texture.
                    ImageCache.SaveImageCache(dest, path);
                }
            }
            catch (FutureFailed e)
            {
                ImageLoadError imageLoad = e.InnerException as ImageLoadError;
                if (imageLoad != null)
                {
                    ControllerConsoleScript.m_Instance.AddNewLine(imageLoad.Message, true);
                }
            }
            finally
            {
                // Reduce the reference count again. This ensures the image gets properly released if
                // ReturnImageFullSize is called before loading finished.
                ReleaseImageFullsize();
            }
        }

        /// Like RequestLoad, but synchronous.
        public void SynchronousLoad()
        {
            // TODO: this is a terrible and dangerous way to do a blocking load
            // In particular, RequestLoad says that "allow main thread" will be ignored
            // if the load's already started. So, try and avoid the infinite loop where
            // RequestLoad keeps failing because we've already created too many this frame.
            int consumedTexturesCreated = 0;
            try
            {
                while (!RequestLoad(true))
                {
                    // Keep this zero to try and avoid an infinite loop.
                    consumedTexturesCreated += ReferenceImageCatalog.m_Instance.TexturesCreatedThisFrame;
                    ReferenceImageCatalog.m_Instance.TexturesCreatedThisFrame = 0;
                }
            }
            finally
            {
                ReferenceImageCatalog.m_Instance.TexturesCreatedThisFrame += consumedTexturesCreated;
            }
        }

        // Attempts to load the icon from the cache. Returns true if successful.
        public bool RequestLoadIconCache()
        {
            if (m_Icon == null)
            {
                // Try to load from cache.
                m_Icon = ImageCache.LoadIconCache(FilePath, out m_ImageAspect);
                if (m_Icon != null)
                {
                    m_State = ImageState.Ready;
                    return true;
                }
            }

            return false;
        }

        /// Attempts to load the image.
        /// Returns true if the attempt is complete (whether successful or not), at which point:
        /// - Icon != null
        /// - Valid tells you whether the image is useable/loadable at fullres
        ///
        /// If allowMainThread, allow use of the main thread.
        /// allowMainThread is ignored if a load has already started.
        public bool RequestLoad(bool allowMainThread = false)
        {
            if (m_State == ImageState.Ready)
            {
                // If the state is ready, that means we've already loaded the icon (ie., through the cache).
                Debug.Assert(Icon != null);
                return true;
            }

            // RequestLoad is used as the pump for m_coroutine.  On first call, there's a variety
            // of setup and checks we want to do.
            if (m_State == ImageState.Uninitialized)
            {
                m_State = ImageState.NotReady;

                // If this file is too large for the platform, don't load it.
                if (!ValidateFileSize())
                {
                    m_State = ImageState.ErrorImageTooLarge;
                    ControllerConsoleScript.m_Instance.AddNewLine(
                        FileName + " is too large and could not be loaded.",
                        true);
                    return true;
                }

                if (RequestLoadIconCache())
                {
                    return true;
                }
            }

            if (m_State != ImageState.NotReady)
            {
                Debug.Assert(m_coroutine == null, "Invariant");
                return true;
            }

            Debug.Assert(m_State == ImageState.NotReady, "Invariant");

            if (FilePath.EndsWith(".svg"))
            {
                // TODO Move into the async code path?
                var importer = new RuntimeSVGImporter();
                var tex = importer.ImportAsTexture(FilePath);

                if (!ValidateDimensions(tex.width, tex.height, App.PlatformConfig.ReferenceImagesMaxDimension))
                {
                    m_State = ImageState.ErrorImageTooLarge;
                    Object.Destroy(tex);
                    return true;
                }

                ImageCache.SaveImageCache(tex, FilePath);
                m_ImageAspect = (float)tex.width / tex.height;
                int resizeLimit = App.PlatformConfig.ReferenceImagesResizeDimension;
                if (tex.width > resizeLimit || tex.height > resizeLimit)
                {
                    Texture2D resizedTex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    DownsizeTexture(tex, ref resizedTex, ReferenceImageCatalog.MAX_ICON_TEX_DIMENSION);
                    m_Icon = resizedTex;
                    Object.Destroy(resizedTex);
                }
                else
                {
                    m_Icon = tex;
                }
                ImageCache.SaveIconCache(m_Icon, FilePath, m_ImageAspect);
                m_State = ImageState.Ready;
                return true;
            }

            if (FilePath.EndsWith(".hdr"))
            {
                // TODO Move into the async code path?
                var fileData = File.ReadAllBytes(FilePath);
                RadianceHDRTexture hdr = new RadianceHDRTexture(fileData);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                tex = hdr.texture;

                if (!ValidateDimensions(tex.width, tex.height, App.PlatformConfig.ReferenceImagesMaxDimension))
                {
                    m_State = ImageState.ErrorImageTooLarge;
                    Object.Destroy(tex);
                    return true;
                }

                ImageCache.SaveImageCache(tex, FilePath);
                m_ImageAspect = (float)tex.width / tex.height;
                int resizeLimit = App.PlatformConfig.ReferenceImagesResizeDimension;
                if (tex.width > resizeLimit || tex.height > resizeLimit)
                {
                    Texture2D resizedTex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    DownsizeTexture(tex, ref resizedTex, ReferenceImageCatalog.MAX_ICON_TEX_DIMENSION);
                    m_Icon = resizedTex;
                    Object.Destroy(resizedTex);
                }
                else
                {
                    m_Icon = tex;
                }
                ImageCache.SaveIconCache(m_Icon, FilePath, m_ImageAspect);
                m_State = ImageState.Ready;
                return true;
            }

            if (m_coroutine == null)
            {
                if (allowMainThread)
                {
                    m_coroutine = RequestLoadCoroutineMainThread();
                }
                else
                {
                    m_coroutine = RequestLoadCoroutine().GetEnumerator();
                }
                // First RequestLoad() should not do any actual work
                return false;
            }

            bool finished = !m_coroutine.MoveNext();
            if (finished)
            {
                m_coroutine = null;
                Debug.Assert(m_State != ImageState.NotReady, "Invariant");
            }
            else
            {
                Debug.Assert(m_State == ImageState.NotReady, "Invariant");
            }

            return finished;
        }

        /// Unloads the icon and sets the reference image to be in a 'not ready' state.
        /// Removes any references pointing this way from existing ImageWidgets.
        public void Unload()
        {
            foreach (var widget in WidgetManager.m_Instance.ImageWidgets
                .Where(x => x.ReferenceImage == this))
            {
                widget.ReferenceImage = null;
            }
            m_coroutine = null; // will the thread get GC'd?
            UnityEngine.Object.Destroy(m_Icon);
            m_Icon = null;
            m_State = ImageState.Uninitialized;
        }

        /// Internal. Copies inTex into outTex, throwing away high hips until
        /// height < maxHeight.
        /// - Textures must be RGBA32
        /// - maxHeight must be reasonable (>= 16)
        /// - inTex must have a valid, full mip chain
        static void DownsizeTexture(Texture2D inTex, ref Texture2D outTex, int maxHeight)
        {
            maxHeight = Mathf.Max(maxHeight, 16);

            int mip = 0;
            while ((inTex.height >> mip) > maxHeight)
            {
                ++mip;
            }
            Debug.Assert(mip < inTex.mipmapCount);

            int outWidth = Mathf.Max(1, inTex.width >> mip);
            int outHeight = Mathf.Max(1, inTex.height >> mip);
            if (outTex == null)
            {
                outTex = new Texture2D(outWidth, outHeight, TextureFormat.RGBA32, true);
            }
            else
            {
                outTex.Reinitialize(outWidth, outHeight, TextureFormat.RGBA32, true);
            }

            // Copy the data, starting from mip
            for (int inMip = mip; inMip < inTex.mipmapCount; inMip++)
            {
                var data = inTex.GetPixels32(inMip);
                outTex.SetPixels32(data, inMip - mip);
            }
            outTex.Apply(false);
        }

        // Returns a string suitable for passing to Unity.WWW.
        static string PathToWwwUrl(string path)
        {
            // WWW.EscapeURL is not appropriate, because it escapes URL parameters.
            // We want escaping for URI path components.
            // For example, " " should not turn into "+".
            // Tested on {}[];'.,`~!@#$%^&()-_=+.png
            // and on filenames with non-latin1 unicode characters
            return "file:///" + path.Replace("%", "%25").Replace("#", "%23").Replace("&", "%26");
        }

        // Like RequestLoadCoroutine, but allowed to use main thread CPU time
        IEnumerator<Timeslice> RequestLoadCoroutineMainThread()
        {
            // On main thread! Can decode images using WWW class. This is about 10x faster
            using (WWW loader = new WWW(PathToWwwUrl(m_Path)))
            {
                while (!loader.isDone)
                {
                    yield return null;
                }
                if (string.IsNullOrEmpty(loader.error))
                {
                    // Passing in a texture with mipmapCount > 1 is how you ask for mips
                    // from WWW.LoadImageIntoTexture
                    Texture2D inTex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    loader.LoadImageIntoTexture(inTex);

                    if (!ValidateDimensions(inTex.width, inTex.height, App.PlatformConfig.ReferenceImagesMaxDimension))
                    {
                        m_State = ImageState.ErrorImageTooLarge;
                        Object.Destroy(inTex);
                        yield break;
                    }

                    DownsizeTexture(inTex, ref m_Icon, ReferenceImageCatalog.MAX_ICON_TEX_DIMENSION);
                    m_Icon.wrapMode = TextureWrapMode.Clamp;
                    m_ImageAspect = (float)inTex.width / inTex.height;
                    ImageCache.SaveIconCache(m_Icon, FilePath, m_ImageAspect);
                    yield return null;

                    // Create the full size image cache as well.
                    int resizeLimit = App.PlatformConfig.ReferenceImagesResizeDimension;
                    if (inTex.width > resizeLimit || inTex.height > resizeLimit)
                    {
                        Texture2D resizedTex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                        DownsizeTexture(inTex, ref resizedTex, resizeLimit);
                        ImageCache.SaveImageCache(resizedTex, m_Path);
                        Object.Destroy(resizedTex);
                    }
                    else
                    {
                        ImageCache.SaveImageCache(inTex, m_Path);
                    }
                    Object.Destroy(inTex);
                    m_State = ImageState.Ready;
                    yield break;
                }
            }

            // OK, take the slower path instead.
            foreach (var ret in RequestLoadCoroutine())
            {
                yield return ret;
            }
        }

        IEnumerable<Timeslice> RequestLoadCoroutine()
        {
            var reader = new ThreadedImageReader(m_Path,
                ReferenceImageCatalog.MAX_ICON_TEX_DIMENSION,
                App.PlatformConfig.ReferenceImagesMaxDimension);
            while (!reader.Finished)
            {
                yield return null;
            }

            RawImage result = null;
            try
            {
                result = reader.Result;
            }
            catch (FutureFailed e)
            {
                ImageLoadError imageLoad = e.InnerException as ImageLoadError;
                if (imageLoad != null)
                {
                    ControllerConsoleScript.m_Instance.AddNewLine(imageLoad.Message, true);
                    if (imageLoad.imageLoadErrorCode == ImageLoadError.ImageLoadErrorCode.ImageTooLargeError)
                    {
                        m_State = ImageState.ErrorImageTooLarge;
                    }
                    else
                    {
                        m_State = ImageState.Error;
                    }

                    reader = null;
                    yield break;

                }
            }

            if (result != null)
            {
                while (ReferenceImageCatalog.m_Instance.TexturesCreatedThisFrame >=
                    ReferenceImageCatalog.TEXTURE_CREATIONS_PER_FRAME)
                {
                    yield return null;
                }

                if (m_Icon == null)
                {
                    m_Icon = new Texture2D(result.ColorWidth, result.ColorHeight, TextureFormat.RGBA32, true);
                }
                else
                {
                    m_Icon.Reinitialize(result.ColorWidth, result.ColorHeight, TextureFormat.RGBA32, true);
                }
                m_ImageAspect = result.ColorAspect;
                m_Icon.wrapMode = TextureWrapMode.Clamp;
                m_Icon.SetPixels32(result.ColorData);
                m_Icon.Apply();
                ReferenceImageCatalog.m_Instance.TexturesCreatedThisFrame++;
                reader = null;
                yield return null;
            }
            else
            {
                // Problem reading the file?
                m_State = ImageState.Error;
                reader = null;
                yield break;
            }

            m_State = ImageState.Ready;
            ImageCache.SaveIconCache(m_Icon, FilePath, m_ImageAspect);
        }

        private bool ValidateFileSize()
        {
            FileInfo info = new FileInfo(m_Path);
            return info.Length <= App.PlatformConfig.ReferenceImagesMaxFileSize;
        }

        private bool ValidateDimensions(int imageWidth, int imageHeight, int maxDimension)
        {

            // Cast to long as maxDimension is big enough on desktop to overflow
            if (imageWidth * imageHeight > ((long)maxDimension * (long)maxDimension))
            {
                return false;
            }
            else
            {
                return true;
            }

        }

        public string GetExportName()
        {
            return Path.GetFileNameWithoutExtension(FileName);
        }
    }

} // namespace TiltBrush
