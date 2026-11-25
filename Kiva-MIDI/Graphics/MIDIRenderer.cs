using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using SharpDX.Direct3D;
using System.Collections.Concurrent;
using IO = System.IO;
using System.Reflection;
using System.Threading;
using Kiva.MIDI;
using Kiva.Util;

namespace Kiva.Graphics
{
    class MIDIRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        struct NotesGlobalConstants
        {
            public float NoteLeft;
            public float NoteRight;
            public float NoteBorder;
            public float ScreenAspect;
            public float KeyboardHeight;
            public int ScreenWidth;
            public int ScreenHeight;
            public int NeonMode;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        struct KeyboardGlobalConstants
        {
            public float Height;
            public float Left;
            public float Right;
            public float Aspect;
            public uint BarColor;
            public int ScreenWidth;
            public int ScreenHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RenderNote
        {
            public float start;
            public float end;
            public NoteCol color;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ParticleVertex
        {
            public float x;
            public float y;
            public float velocityX;
            public float velocityY;
            public float age;
            public float size;
            public uint color;
        }

        struct ActiveParticle
        {
            public float x;
            public float y;
            public float velocityX;
            public float velocityY;
            public float size;
            public uint color;
            public double birthTime;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        struct ParticleGlobalConstants
        {
            public float ScreenAspect;
            public float Padding1;
            public float Padding2;
            public float Padding3;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RenderKey
        {
            public uint colorl;
            public uint colorr;
            public float left;
            public float right;
            public float distance;
            public uint meta;

            public void MarkPressed(bool ispressed)
            {
                meta = (uint)(meta & 0b11111101);
                if (ispressed)
                    meta = (uint)(meta | 0b10);
            }

            public void MarkBlack(bool black)
            {
                meta = (uint)(meta & 0b11111110);
                if (black)
                    meta = (uint)(meta | 0b1);
            }
        }

        private MIDIFile file;
        public MIDIFile File
        {
            get => file;
            set
            {
                lock (fileLock)
                {
                    file = value;
                }
            }
        }

        object fileLock = new object();
        public GlobalTime Time
        {
            get => _time;
            set
            {
                if (Time != null)
                    Time.TimeChanged -= onTimeChanged;
                _time = value;
                Time.TimeChanged += onTimeChanged;
            }
        }
        public long LastRenderedNoteCount { get; private set; } = 0;
        public long LastNPS => notesPassedPerFrame.Sum();
        public long LastPolyphony { get; private set; } = 0;
        public long NotesPassedSum { get; private set; } = 0;

        FastList<int> notesPassedPerFrame = new FastList<int>();
        FastList<double> notesPassedTimes = new FastList<double>();

        bool timeChanged = false;
        bool resetNps = false;

        ShaderManager notesShader;
        ShaderManager SmallWhiteKeyShader;
        ShaderManager SmallBlackKeyShader;
        ShaderManager BigWhiteKeyShader;
        ShaderManager BigBlackKeyShader;
        ShaderManager BigBarShader;
        ShaderManager particleShader;
        InputLayout noteLayout;
        InputLayout keyLayout;
        InputLayout particleLayout;
        Buffer globalNoteConstants;
        Buffer particleConstants;
        Buffer particleBuffer;
        Buffer keyBuffer;

        NotesGlobalConstants noteConstants;

        int noteBufferLength = 1 << 12;
        Buffer noteBuffer;
        const int ParticleCapacity = 4096;
        const int ParticlesPerImpact = 9;
        const double ParticleLifetime = 0.75;
        readonly List<ActiveParticle> activeParticles = new List<ActiveParticle>();
        readonly ParticleVertex[] particleVertices = new ParticleVertex[ParticleCapacity];
        readonly object particleLock = new object();
        MIDIFile particleFile;

        VelocityEase dynamicState = new VelocityEase(0) { Duration = 0.7, Slope = 3, Supress = 2 };
        bool dynamicState88 = false;

        bool[] blackKeys = new bool[257];
        int[] keynum = new int[257];
        int[] blackKeysID;
        int[] whiteKeysID;

        RenderKey[] renderKeys = new RenderKey[257];
        VelocityEase[] keyEases = new VelocityEase[256];

        KivaSettings settings;

        DisposeGroup dispose = new DisposeGroup();

        public MIDIRenderer(Device device, KivaSettings settings)
        {
            this.settings = settings;

            string GetShaderCode(string filename, string resourceName)
            {
                if (IO.File.Exists(filename))
                {
                    return IO.File.ReadAllText(filename);
                }
                else
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var names = assembly.GetManifestResourceNames();
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    using (var reader = new IO.StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }

            string noteShaderData = GetShaderCode("Notes.fx", "Kiva.Notes.fx");
            notesShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(noteShaderData, "VS_Note", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(noteShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(noteShaderData, "GS_Note", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));

            string particleShaderData = GetShaderCode("Particles.fx", "Kiva.Particles.fx");
            particleShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(particleShaderData, "VS_Particle", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(particleShaderData, "PS_Particle", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(particleShaderData, "GS_Particle", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));

            string keyboardSmallShaderData = GetShaderCode("KeyboardSmall.fx", "Kiva.KeyboardSmall.fx");
            SmallWhiteKeyShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(keyboardSmallShaderData, "VS", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardSmallShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardSmallShaderData, "GS_White", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));
            SmallBlackKeyShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(keyboardSmallShaderData, "VS", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardSmallShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardSmallShaderData, "GS_Black", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));

            string keyboardBigShaderData = GetShaderCode("KeyboardBig.fx", "Kiva.KeyboardBig.fx");
            BigWhiteKeyShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(keyboardBigShaderData, "VS", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardBigShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardBigShaderData, "GS_White", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));
            BigBlackKeyShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(keyboardBigShaderData, "VS", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardBigShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardBigShaderData, "GS_Black", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));
            BigBarShader = dispose.Add(new ShaderManager(
                device,
                ShaderBytecode.Compile(keyboardBigShaderData, "VS", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardBigShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(keyboardBigShaderData, "GS_Bar", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            ));

            noteLayout = dispose.Add(new InputLayout(device, ShaderSignature.GetInputSignature(notesShader.vertexShaderByteCode), new[] {
                new InputElement("START",0,Format.R32_Float,0),
                new InputElement("END",0,Format.R32_Float,0),
                new InputElement("COLORL",0,Format.R32_UInt,0),
                new InputElement("COLORR",0,Format.R32_UInt,0),
            }));

            keyLayout = dispose.Add(new InputLayout(device, ShaderSignature.GetInputSignature(SmallWhiteKeyShader.vertexShaderByteCode), new[] {
                new InputElement("COLORL",0,Format.R32_UInt,0),
                new InputElement("COLORR",0,Format.R32_UInt,0),
                new InputElement("LEFT",0,Format.R32_Float,0),
                new InputElement("RIGHT",0,Format.R32_Float,0),
                new InputElement("DISTANCE",0,Format.R32_Float,0),
                new InputElement("META",0,Format.R32_UInt,0),
            }));

            particleLayout = dispose.Add(new InputLayout(device, ShaderSignature.GetInputSignature(particleShader.vertexShaderByteCode), new[] {
                new InputElement("POSITION",0,Format.R32G32_Float,0),
                new InputElement("VELOCITY",0,Format.R32G32_Float,8),
                new InputElement("AGE",0,Format.R32_Float,16),
                new InputElement("SIZE",0,Format.R32_Float,20),
                new InputElement("COLOR",0,Format.R32_UInt,24),
            }));

            noteConstants = new NotesGlobalConstants()
            {
                NoteBorder = 0.002f,
                NoteLeft = -0.2f,
                NoteRight = 0.0f,
                ScreenAspect = 1f
            };

            noteBuffer = dispose.Add(new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 40 * noteBufferLength,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            }));

            keyBuffer = dispose.Add(new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 24 * renderKeys.Length,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            }));

            globalNoteConstants = dispose.Add(new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 32,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            }));

            particleConstants = dispose.Add(new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 16,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            }));

            particleBuffer = dispose.Add(new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = Marshal.SizeOf(typeof(ParticleVertex)) * ParticleCapacity,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            }));

            for (int i = 0; i < blackKeys.Length; i++) blackKeys[i] = isBlackNote(i);
            int b = 0;
            int w = 0;
            List<int> black = new List<int>();
            List<int> white = new List<int>();
            for (int i = 0; i < keynum.Length; i++)
            {
                if (blackKeys[i])
                {
                    keynum[i] = b++;
                    if (i < 256)
                        black.Add(i);
                }
                else
                {
                    keynum[i] = w++;
                    if (i < 256)
                        white.Add(i);
                }
            }

            blackKeysID = black.ToArray();
            whiteKeysID = white.ToArray();

            int firstNote = 0;
            int lastNote = 256;

            double wdth;

            double blackKeyScale = 0.65;
            double offset2set = 0.3;
            double offset3set = 0.5;

            double knmfn = keynum[firstNote];
            double knmln = keynum[lastNote - 1];
            if (blackKeys[firstNote]) knmfn = keynum[firstNote - 1] + 0.5;
            if (blackKeys[lastNote - 1]) knmln = keynum[lastNote] - 0.5;
            for (int i = 0; i < 257; i++)
            {
                if (!blackKeys[i])
                {
                    x1array[i] = (float)(keynum[i] - knmfn) / (knmln - knmfn + 1);
                    wdtharray[i] = 1.0f / (knmln - knmfn + 1);
                }
                else
                {
                    int _i = i + 1;
                    wdth = blackKeyScale / (knmln - knmfn + 1);
                    int bknum = keynum[i] % 5;
                    double offset = wdth / 2;
                    if (bknum == 0) offset += offset * offset2set;
                    if (bknum == 2) offset += offset * offset3set;
                    if (bknum == 1) offset -= offset * offset2set;
                    if (bknum == 4) offset -= offset * offset3set;

                    x1array[i] = (float)(keynum[_i] - knmfn) / (knmln - knmfn + 1) - offset;
                    wdtharray[i] = wdth;
                    renderKeys[i].MarkBlack(true);
                }
                renderKeys[i].left = (float)x1array[i];
                renderKeys[i].right = (float)(x1array[i] + wdtharray[i]);
            }

            for (int i = 0; i < keyEases.Length; i++)
            {
                keyEases[i] = new VelocityEase(0) { Duration = 0.05, Slope = 2, Supress = 3 };
            }

            var renderTargetDesc = new RenderTargetBlendDescription();
            renderTargetDesc.IsBlendEnabled = true;
            renderTargetDesc.SourceBlend = BlendOption.SourceAlpha;
            renderTargetDesc.DestinationBlend = BlendOption.InverseSourceAlpha;
            renderTargetDesc.BlendOperation = BlendOperation.Add;
            renderTargetDesc.SourceAlphaBlend = BlendOption.One;
            renderTargetDesc.DestinationAlphaBlend = BlendOption.One;
            renderTargetDesc.AlphaBlendOperation = BlendOperation.Add;
            renderTargetDesc.RenderTargetWriteMask = ColorWriteMaskFlags.All;

            BlendStateDescription desc = new BlendStateDescription();
            desc.AlphaToCoverageEnable = false;
            desc.IndependentBlendEnable = false;
            desc.RenderTarget[0] = renderTargetDesc;

            var blendStateEnabled = dispose.Add(new BlendState(device, desc));

            device.ImmediateContext.OutputMerger.SetBlendState(blendStateEnabled);

            RasterizerStateDescription renderStateDesc = new RasterizerStateDescription
            {
                CullMode = CullMode.None,
                DepthBias = 0,
                DepthBiasClamp = 0,
                FillMode = FillMode.Solid,
                IsAntialiasedLineEnabled = false,
                IsDepthClipEnabled = false,
                IsFrontCounterClockwise = false,
                IsMultisampleEnabled = true,
                IsScissorEnabled = false,
                SlopeScaledDepthBias = 0
            };
            var rasterStateSolid = dispose.Add(new RasterizerState(device, renderStateDesc));
            device.ImmediateContext.Rasterizer.State = rasterStateSolid;
        }

        void onTimeChanged() => timeChanged = true;

        void SetNoteShaderConstants(DeviceContext context, NotesGlobalConstants constants)
        {
            DataStream data;
            context.MapSubresource(globalNoteConstants, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
            data.Write(constants);
            context.UnmapSubresource(globalNoteConstants, 0);
            context.VertexShader.SetConstantBuffer(0, globalNoteConstants);
            context.GeometryShader.SetConstantBuffer(0, globalNoteConstants);
            data.Dispose();
        }

        void SetKeyboardShaderConstants(DeviceContext context, KeyboardGlobalConstants constants)
        {
            DataStream data;
            context.MapSubresource(globalNoteConstants, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
            data.Write(constants);
            context.UnmapSubresource(globalNoteConstants, 0);
            context.VertexShader.SetConstantBuffer(0, globalNoteConstants);
            context.GeometryShader.SetConstantBuffer(0, globalNoteConstants);
            data.Dispose();
        }

        double[] x1array = new double[257];
        double[] wdtharray = new double[257];
        bool[] pressedKeys = new bool[256];
        private GlobalTime _time = new GlobalTime();
        object addLock = new object();

        public void Render(Device device, RenderTargetView target, DrawEventArgs args)
        {
            var context = device.ImmediateContext;
            context.InputAssembler.InputLayout = noteLayout;

            double time = Time.GetTimeInt();
            double timeInSeconds = time / Constants.TicksPerSecond;
            bool particlesReset = timeChanged;
            if (particlesReset || !ReferenceEquals(particleFile, File))
            {
                activeParticles.Clear();
                particleFile = File;
            }
            var spawnedParticles = new List<ActiveParticle>();
            double timeScale = Constants.TimeToInt(settings.Volatile.Size);
            double renderCutoff = time + timeScale;
            int firstNote = 0;
            int lastNote = 128;
            if (settings.General.KeyRange == KeyRangeTypes.KeyDynamic ||
                settings.General.KeyRange == KeyRangeTypes.Key128)
            {
                firstNote = 0;
                lastNote = 128;
            }
            else if (settings.General.KeyRange == KeyRangeTypes.Key256)
            {
                firstNote = 0;
                lastNote = 256;
            }
            else if (settings.General.KeyRange == KeyRangeTypes.Key88)
            {
                firstNote = 21;
                lastNote = 109;
            }
            else if (settings.General.KeyRange == KeyRangeTypes.Custom)
            {
                firstNote = settings.General.CustomFirstKey;
                lastNote = settings.General.CustomLastKey + 1;
            }
            else if (settings.General.KeyRange == KeyRangeTypes.KeyMIDI)
            {
                if (File != null)
                {
                    firstNote = File.FirstKey;
                    lastNote = File.LastKey + 1;
                }
            }
            int kbfirstNote = firstNote;
            int kblastNote = lastNote;
            if (blackKeys[firstNote]) kbfirstNote--;
            if (blackKeys[lastNote - 1]) kblastNote++;


            notesShader.SetShaders(context);
            noteConstants.ScreenAspect = (float)(args.RenderSize.Height / args.RenderSize.Width);
            noteConstants.NoteBorder = 0.0015f;
            noteConstants.ScreenWidth = (int)args.RenderSize.Width;
            noteConstants.ScreenHeight = (int)args.RenderSize.Height;
            noteConstants.NeonMode = settings.General.NeonMode ? 1 : 0;
            SetNoteShaderConstants(context, noteConstants);

            //context.ClearRenderTargetView(target, new Color4(0.4f, 0.4f, 0.4f, 1f));
            context.ClearRenderTargetView(target, new Color4(0.0f, 0.0f, 0.0f, 0f));

            double ds = dynamicState.GetValue(0, 1);

            double fullLeft = x1array[firstNote];
            double fullRight = x1array[lastNote - 1] + wdtharray[lastNote - 1];
            if (settings.General.KeyRange == KeyRangeTypes.KeyDynamic)
            {
                double kleft = x1array[21];
                double kright = x1array[108] + wdtharray[108];
                fullLeft = fullLeft * (1 - ds) + kleft * ds;
                fullRight = fullRight * (1 - ds) + kright * ds;
            }
            double fullWidth = fullRight - fullLeft;

            float kbHeight = (float)(args.RenderSize.Width / args.RenderSize.Height / fullWidth);
            if (settings.General.KeyboardStyle == KeyboardStyle.Small) kbHeight *= 0.017f;
            if (settings.General.KeyboardStyle == KeyboardStyle.Big) kbHeight *= 0.04f;
            if (settings.General.KeyboardStyle == KeyboardStyle.None) kbHeight = 0;
            noteConstants.KeyboardHeight = kbHeight;

            lock (fileLock)
            {
                if (File != null)
                {
                    if (File is MIDINoteFile)
                    {
                        var file = File as MIDINoteFile;
                        file.SetColorEvents(time);

                        var colors = file.MidiNoteColors;
                        var lastTime = file.lastRenderTime;

                        long notesRendered = 0;
                        int polyphonySum = 0;
                        int notesHitSum = 0;

                        int firstRenderKey = 256;
                        int lastRenderKey = -1;

                        int[] ids;

                        for (int black = 0; black < 2; black++)
                        {
                            if (black == 1) ids = blackKeysID;
                            else ids = whiteKeysID;
                            RenderLoop(ids, k =>
                            {
                                long _notesRendered = 0;
                                float left = (float)((x1array[k] - fullLeft) / fullWidth);
                                float right = (float)((x1array[k] + wdtharray[k] - fullLeft) / fullWidth);
                                bool pressed = false;
                                NoteCol col = new NoteCol();
                                int lastHitNote = file.FirstUnhitNote[k] - 1;
                                unsafe
                                {
                                    RenderNote* rn = stackalloc RenderNote[noteBufferLength];
                                    int nid = 0;
                                    int noff = file.FirstRenderNote[k];
                                    int polyphony = 0;
                                    Note[] notes = file.Notes[k];
                                    if (notes.Length == 0) goto skipLoop;
                                    if (lastTime > time)
                                    {
                                        for (noff = 0; noff < notes.Length; noff++)
                                        {
                                            if (notes[noff].end > time)
                                            {
                                                break;
                                            }
                                        }
                                        file.FirstRenderNote[k] = noff;
                                    }
                                    else if (lastTime < time)
                                    {
                                        for (; noff < notes.Length; noff++)
                                        {
                                            if (notes[noff].end > time)
                                            {
                                                break;
                                            }
                                        }
                                        file.FirstRenderNote[k] = noff;
                                    }
                                    while (noff != notes.Length && notes[noff].start < renderCutoff)
                                    {
                                        var n = notes[noff++];
                                        if (n.end < time)
                                        {
                                            lastHitNote = noff - 1;
                                            continue;
                                        }
                                        if (n.start < time)
                                        {
                                            polyphony++;
                                            pressed = true;
                                            NoteCol kcol = file.MidiNoteColors[n.colorPointer];
                                            col.rgba = NoteCol.Blend(col.rgba, kcol.rgba);
                                            col.rgba2 = NoteCol.Blend(col.rgba2, kcol.rgba2);
                                            lastHitNote = noff - 1;
                                            if (!particlesReset &&
                                                time > lastTime &&
                                                time - lastTime <= Constants.TimeToInt(0.25) &&
                                                n.start >= lastTime &&
                                                n.start < time)
                                            {
                                                AddImpactParticles(spawnedParticles, k, left, right, kbHeight, n, colors[n.colorPointer]);
                                            }
                                        }
                                        _notesRendered++;
                                        rn[nid++] = new RenderNote()
                                        {
                                            start = (float)((n.start - time) / timeScale),
                                            end = (float)((n.end - time) / timeScale),
                                            color = colors[n.colorPointer]
                                        };
                                        if (nid == noteBufferLength)
                                        {
                                            FlushNoteBuffer(context, left, right, (IntPtr)rn, nid);
                                            nid = 0;
                                        }
                                    }
                                    FlushNoteBuffer(context, left, right, (IntPtr)rn, nid);
                                    skipLoop:
                                    renderKeys[k].colorl = col.rgba;
                                    renderKeys[k].colorr = col.rgba2;
                                    renderKeys[k].MarkPressed(pressed);
                                    if (pressed && keyEases[k].End == 0) keyEases[k].SetEnd(1);
                                    else if (!pressed && keyEases[k].End == 1) keyEases[k].SetEnd(0);
                                    renderKeys[k].distance = (float)keyEases[k].GetValue(0, 1);
                                    if (_notesRendered == 0) lastHitNote = file.FirstRenderNote[k] - 1;
                                    lock (addLock)
                                    {
                                        polyphonySum += polyphony;
                                        notesHitSum += lastHitNote - file.FirstUnhitNote[k] + 1;
                                        notesRendered += _notesRendered;
                                        if (_notesRendered > 0)
                                        {
                                            if (firstRenderKey > k) firstRenderKey = k;
                                            if (lastRenderKey < k) lastRenderKey = k;
                                        }
                                    }
                                    file.FirstUnhitNote[k] = lastHitNote + 1;
                                }
                            });
                        }
                        if (firstRenderKey <= 19 || lastRenderKey >= 110)
                        {
                            if (dynamicState88)
                            {
                                dynamicState.SetEnd(0);
                                dynamicState88 = false;
                            }
                        }
                        else
                        {
                            if (!dynamicState88)
                            {
                                dynamicState.SetEnd(1);
                                dynamicState88 = true;
                            }
                        }

                        if (notesHitSum < 0) notesHitSum = 0;
                        notesPassedPerFrame.Add(notesHitSum);
                        notesPassedTimes.Add(time);
                        while (!notesPassedTimes.ZeroLen && notesPassedTimes.First < time - 1)
                        {
                            notesPassedPerFrame.Pop();
                            notesPassedTimes.Pop();
                        }
                        if (timeChanged || resetNps)
                        {
                            notesPassedPerFrame.Unlink();
                            notesPassedTimes.Unlink();
                            timeChanged = false;
                            if (!resetNps) resetNps = true;
                            else resetNps = false;
                        }
                        LastRenderedNoteCount = notesRendered;
                        LastPolyphony = polyphonySum;
                        NotesPassedSum = file.FirstUnhitNote.Select(s => s).Sum();
                        file.lastRenderTime = time;
                    }
                }
                else
                {
                    if (!notesPassedTimes.ZeroLen)
                    {
                        notesPassedPerFrame.Unlink();
                        notesPassedTimes.Unlink();
                    }
                    LastRenderedNoteCount = 0;
                    LastPolyphony = 0;
                    NotesPassedSum = 0;
                    for (int i = 0; i < renderKeys.Length; i++)
                    {
                        renderKeys[i].colorl = 0;
                        renderKeys[i].colorr = 0;
                        renderKeys[i].distance = 0;
                    }
                }
            }

            activeParticles.AddRange(spawnedParticles);
            if (activeParticles.Count > ParticleCapacity)
                activeParticles.RemoveRange(0, activeParticles.Count - ParticleCapacity);

            if (settings.General.KeyboardStyle != KeyboardStyle.None)
            {
                DataStream data;
                var col = settings.General.BarColor;
                SetKeyboardShaderConstants(context, new KeyboardGlobalConstants()
                {
                    Height = kbHeight,
                    Left = (float)fullLeft,
                    Right = (float)fullRight,
                    Aspect = noteConstants.ScreenAspect,
                    BarColor = NoteCol.Compress(col.R, col.G, col.B, col.A),
                    ScreenWidth = (int)args.RenderSize.Width,
                    ScreenHeight = (int)args.RenderSize.Height
                });
                context.InputAssembler.InputLayout = keyLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(keyBuffer, 24, 0));
                context.MapSubresource(keyBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
                data.Position = 0;
                data.WriteRange(renderKeys, 0, 257);
                context.UnmapSubresource(keyBuffer, 0);
                data.Close();
                if (settings.General.KeyboardStyle == KeyboardStyle.Big)
                {
                    BigWhiteKeyShader.SetShaders(context);
                    context.Draw(kblastNote - kbfirstNote, kbfirstNote);
                    BigBarShader.SetShaders(context);
                    context.Draw(1, 0);
                    BigBlackKeyShader.SetShaders(context);
                    context.Draw(kblastNote - kbfirstNote, kbfirstNote);
                }
                else
                {
                    SmallWhiteKeyShader.SetShaders(context);
                    context.Draw(kblastNote - kbfirstNote, kbfirstNote);
                    SmallBlackKeyShader.SetShaders(context);
                    context.Draw(kblastNote - kbfirstNote, kbfirstNote);
                }
            }

            RenderParticles(context, timeInSeconds, args.RenderSize.Width, args.RenderSize.Height);
        }

        void AddImpactParticles(List<ActiveParticle> spawned, int key, float left, float right, float keyboardHeight, Note note, NoteCol color)
        {
            uint random = unchecked((uint)note.start * 747796405u + (uint)key * 2891336453u + 277803737u);
            lock (particleLock)
            {
                if (spawned.Count + ParticlesPerImpact > ParticleCapacity) return;
                for (int i = 0; i < ParticlesPerImpact; i++)
                {
                    float horizontal = NextParticleRandom(ref random);
                    float upward = NextParticleRandom(ref random);
                    float size = NextParticleRandom(ref random);
                    spawned.Add(new ActiveParticle()
                    {
                        x = left + (right - left) * (0.25f + horizontal * 0.5f),
                        y = keyboardHeight,
                        velocityX = (horizontal - 0.5f) * 0.8f,
                        velocityY = -0.25f - upward * 0.55f,
                        size = 0.0025f + size * 0.003f,
                        color = color.rgba,
                        birthTime = (double)note.start / Constants.TicksPerSecond
                    });
                }
            }
        }

        static float NextParticleRandom(ref uint state)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            return (state & 0x00ffffff) / 16777216.0f;
        }

        void RenderParticles(DeviceContext context, double timeInSeconds, double width, double height)
        {
            int count = 0;
            for (int i = activeParticles.Count - 1; i >= 0; i--)
            {
                var particle = activeParticles[i];
                double age = timeInSeconds - particle.birthTime;
                if (age < 0 || age >= ParticleLifetime)
                {
                    activeParticles.RemoveAt(i);
                    continue;
                }
                particleVertices[count++] = new ParticleVertex()
                {
                    x = particle.x,
                    y = particle.y,
                    velocityX = particle.velocityX,
                    velocityY = particle.velocityY,
                    age = (float)age,
                    size = particle.size,
                    color = particle.color
                };
            }
            if (count == 0 || width <= 0 || height <= 0) return;

            DataStream data;
            context.MapSubresource(particleConstants, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
            data.Write(new ParticleGlobalConstants() { ScreenAspect = (float)(height / width) });
            context.UnmapSubresource(particleConstants, 0);
            data.Dispose();

            context.VertexShader.SetConstantBuffer(0, particleConstants);
            context.GeometryShader.SetConstantBuffer(0, particleConstants);
            particleShader.SetShaders(context);
            context.InputAssembler.InputLayout = particleLayout;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
            context.MapSubresource(particleBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
            data.Position = 0;
            data.WriteRange(particleVertices, 0, count);
            context.UnmapSubresource(particleBuffer, 0);
            data.Dispose();
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(particleBuffer, Marshal.SizeOf(typeof(ParticleVertex)), 0));
            context.Draw(count, 0);
        }

        bool singleThreaded = false;
        void RenderLoop(int[] ids, Action<int> render)
        {
            singleThreaded = settings.General.MultiThreadedRendering;
            if (settings.General.MultiThreadedRendering)
            {
                Parallel.ForEach(ids, new ParallelOptions() { MaxDegreeOfParallelism = settings.General.MaxRenderThreads }, render);
            }
            else
            {
                foreach (var i in ids) render(i);
            }
        }

        unsafe void FlushNoteBuffer(DeviceContext context, float left, float right, IntPtr notes, int count)
        {
            if (count == 0) return;
            if (singleThreaded) Monitor.Enter(context);
            DataStream data;
            context.MapSubresource(noteBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
            data.Position = 0;
            data.WriteRange(notes, count * sizeof(RenderNote));
            context.UnmapSubresource(noteBuffer, 0);
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(noteBuffer, 16, 0));
            noteConstants.NoteLeft = left;
            noteConstants.NoteRight = right;
            SetNoteShaderConstants(context, noteConstants);
            context.Draw(count, 0);
            data.Dispose();
            if (singleThreaded) Monitor.Exit(context);
        }

        private DeviceContext GetInternalContext(Device device)
        {
            var props = device.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var prop = device.GetType().GetField("Context", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            DeviceContext context = prop.GetValue(device) as DeviceContext;
            return context;
        }

        public void Dispose()
        {
            dispose.Dispose();

            Time.TimeChanged -= onTimeChanged;
        }

        bool isBlackNote(int n)
        {
            n = n % 12;
            return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
        }
    }
}
