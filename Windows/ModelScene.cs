using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Gloria {
    // Hardware-accelerated WPF Viewport3D renderer for GrokBot & Fourteen.
    // Freezes all static meshes & textures, interpolates poses smoothly, and stops invalidating
    // the 3D scene when settled so idle pets consume 0% GPU/CPU.
    internal sealed class ModelScene : Viewport3D {
        sealed class Rig {
            public int format { get; set; }
            public int meshCount { get; set; }
            public double cameraWidth { get; set; }
            public double[] cameraPosition { get; set; }
            public double[] cameraFront { get; set; }
            public double[] cameraUp { get; set; }
            public double[] eyeCenter { get; set; }
            public RigNode[] nodes { get; set; }
            public Surface[] materials { get; set; }
            public Dictionary<string, double[][][]> animations { get; set; }
        }

        sealed class RigNode {
            public string name { get; set; }
            public int parent { get; set; }
            public int mesh { get; set; }
            public double[] rest { get; set; }
        }

        sealed class Surface {
            public string name { get; set; }
            public string texture { get; set; }
            public double[] diffuse { get; set; }
            public double[] emission { get; set; }
            public double alpha { get; set; }
            public bool unlit { get; set; }
            public bool doubleSided { get; set; }
        }

        struct Pose {
            internal Vector3D Position, Scale;
            internal Quaternion Rotation;
            internal bool Hidden;
            internal int Material;

            internal Pose(double[] values, int offset) {
                Position = Vector(values, offset);
                Rotation = new Quaternion(values[offset + 3], values[offset + 4], values[offset + 5], values[offset + 6]);
                Rotation.Normalize();
                Scale = Vector(values, offset + 7);
                Hidden = values[offset + 10] != 0;
                Material = (int)values[offset + 11];
            }

            internal Matrix3D Matrix() {
                var matrix = Matrix3D.Identity;
                matrix.Scale(Scale);
                matrix.Rotate(Rotation);
                matrix.Translate(Position);
                return matrix;
            }

            internal bool Approach(Pose target, double fraction) {
                bool changed = false;
                var delta = target.Position - Position;
                if (delta.LengthSquared < 4e-6) {
                    // Sub-pixel settled (< 0.05 px); avoid dirtying the 3D scene graph
                } else if (fraction >= 0.999) {
                    Position = target.Position;
                    changed = true;
                } else {
                    Position = Position + delta * fraction;
                    changed = true;
                }

                delta = target.Scale - Scale;
                if (delta.LengthSquared < 9e-6) {
                    // Sub-pixel scale breathing (< 0.08 px)
                } else if (fraction >= 0.999) {
                    Scale = target.Scale;
                    changed = true;
                } else {
                    Scale = Scale + delta * fraction;
                    changed = true;
                }

                double dot = Rotation.X * target.Rotation.X + Rotation.Y * target.Rotation.Y
                           + Rotation.Z * target.Rotation.Z + Rotation.W * target.Rotation.W;
                if (Math.Abs(dot) > 0.99996) {
                    // < 0.5 degree settled
                } else if (fraction >= 0.999) {
                    Rotation = target.Rotation;
                    changed = true;
                } else {
                    Rotation = Quaternion.Slerp(Rotation, target.Rotation, fraction);
                    changed = true;
                }

                if (Hidden != target.Hidden) { Hidden = target.Hidden; changed = true; }
                if (Material != target.Material) { Material = target.Material; changed = true; }
                return changed;
            }
        }

        sealed class Part {
            internal readonly Model3DGroup Model = new Model3DGroup();
            internal readonly MatrixTransform3D Transform = new MatrixTransform3D();
            internal Model3DGroup Parent;
            internal GeometryModel3D Geometry;
            internal Pose Current;
        }

        readonly Character art;
        readonly bool fourteen;
        readonly Part[] parts;
        readonly Pose[] rest;
        readonly int[] moving;
        readonly Material[] materials;
        readonly Surface[] surfaces;
        readonly Dictionary<string, Pose[][]> animations = new Dictionary<string, Pose[][]>();
        readonly Dictionary<string, int> named = new Dictionary<string, int>();
        readonly Vector3D eyeCenter;
        readonly Pose[] gaze;
        double lastUpdate = Clock.Now;

        static readonly Vector3D BodyCenter = new Vector3D(0, 0, 1.17);
        static readonly Vector3D Up = new Vector3D(0, 0, 1);
        static readonly Vector3D Side = new Vector3D(1, 0, 0);
        static readonly Vector3D Forward = new Vector3D(0, 1, 0);

        internal bool Settled { get; private set; }

        static Vector3D Vector(double[] values, int index) {
            return new Vector3D(values[index], values[index + 1], values[index + 2]);
        }

        static MeshGeometry3D[] LoadMeshes(string file, int count, bool textured) {
            using (var reader = new BinaryReader(File.OpenRead(file))) {
                if (new string(reader.ReadChars(4)) != (textured ? "PET2" : "GRK1"))
                    throw new InvalidDataException("Invalid model mesh format");
                var meshes = new MeshGeometry3D[count];
                for (int i = 0; i < count; i++) {
                    int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                    var positions = new Point3DCollection(vertices);
                    var normals = new Vector3DCollection(vertices);
                    var triangles = new Int32Collection(indices);
                    var uv = textured ? new PointCollection(vertices) : null;
                    for (int v = 0; v < vertices; v++) {
                        positions.Add(new Point3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                        normals.Add(new Vector3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                        if (textured) uv.Add(new Point(reader.ReadSingle(), reader.ReadSingle()));
                    }
                    for (int j = 0; j < indices; j++) triangles.Add(reader.ReadInt32());
                    var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = triangles };
                    if (textured) mesh.TextureCoordinates = uv;
                    mesh.Freeze();
                    meshes[i] = mesh;
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException("Invalid model mesh length");
                return meshes;
            }
        }

        static SolidColorBrush MakeBrush(double[] rgb, double alpha) {
            var b = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(alpha * 255),
                (byte)Math.Round(rgb[0] * 255),
                (byte)Math.Round(rgb[1] * 255),
                (byte)Math.Round(rgb[2] * 255)));
            b.Freeze();
            return b;
        }

        static Material MakeMaterial(Surface surface, string directory) {
            var result = new MaterialGroup();
            Brush diffuse = MakeBrush(surface.diffuse, surface.alpha);
            if (!String.IsNullOrEmpty(surface.texture)) {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(Path.Combine(directory, surface.texture));
                img.EndInit();
                img.Freeze();
                var ib = new ImageBrush(img) {
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1),
                    TileMode = TileMode.Tile
                };
                ib.Freeze();
                diffuse = ib;
            }
            if (surface.unlit) {
                result.Children.Add(new DiffuseMaterial(MakeBrush(new double[] { 0, 0, 0 }, surface.alpha)));
                result.Children.Add(new EmissiveMaterial(diffuse));
            } else {
                result.Children.Add(new DiffuseMaterial(diffuse));
                var e = surface.emission;
                if (e[0] + e[1] + e[2] > 0) result.Children.Add(new EmissiveMaterial(MakeBrush(e, surface.alpha)));
                if (surface.name == "Core_GlossBlack") result.Children.Add(new SpecularMaterial(Brushes.DimGray, 62));
            }
            result.Freeze();
            return result;
        }

        internal ModelScene(Character character, string id) {
            art = character;
            string directory = art.DirectoryPath;
            fourteen = id == "fourteen";
            IsHitTestVisible = false;
            ClipToBounds = false;

            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var rig = json.Deserialize<Rig>(File.ReadAllText(Path.Combine(directory, id + ".rig.json")));
            if (rig.format != (fourteen ? 2 : 1)) throw new InvalidDataException("Invalid model rig format");

            var meshes = LoadMeshes(Path.Combine(directory, id + ".mesh"), rig.meshCount, fourteen);
            surfaces = rig.materials;
            materials = Array.ConvertAll(surfaces, s => MakeMaterial(s, directory));
            eyeCenter = Vector(rig.eyeCenter, 0);

            var world = new Model3DGroup();
            parts = new Part[rig.nodes.Length];
            rest = new Pose[parts.Length];
            for (int i = 0; i < parts.Length; i++) {
                var node = rig.nodes[i];
                var part = parts[i] = new Part();
                if (!String.IsNullOrEmpty(node.name) && !named.ContainsKey(node.name)) named.Add(node.name, i);
                rest[i] = part.Current = new Pose(node.rest, 0);
                part.Transform.Matrix = part.Current.Matrix();
                part.Model.Transform = part.Transform;
                part.Parent = node.parent < 0 ? world : parts[node.parent].Model;
                if (node.mesh >= 0) {
                    part.Geometry = new GeometryModel3D { Geometry = meshes[node.mesh] };
                    SetMaterial(part, part.Current.Material);
                    part.Model.Children.Add(part.Geometry);
                }
                if (!part.Current.Hidden) part.Parent.Children.Add(part.Model);
            }

            var dynamicParts = new HashSet<int>();
            foreach (var clip in rig.animations) {
                var frames = new Pose[clip.Value.Length][];
                for (int i = 0; i < frames.Length; i++) {
                    frames[i] = (Pose[])rest.Clone();
                    foreach (var row in clip.Value[i]) {
                        int index = (int)row[0];
                        dynamicParts.Add(index);
                        frames[i][index] = new Pose(row, 1);
                    }
                }
                animations.Add(clip.Key, frames);
            }

            foreach (var name in (fourteen ? new[] { "HeadJoint" } : new[] { "Bot_root", "EarPivot_1", "EarPivot1", "EyePivot" })) {
                int idx;
                if (named.TryGetValue(name, out idx)) dynamicParts.Add(idx);
            }
            moving = new int[dynamicParts.Count];
            dynamicParts.CopyTo(moving);
            gaze = (Pose[])rest.Clone();

            Camera = new OrthographicCamera(
                (Point3D)Vector(rig.cameraPosition, 0),
                Vector(rig.cameraFront, 0),
                Vector(rig.cameraUp, 0),
                rig.cameraWidth) { NearPlaneDistance = 0.1, FarPlaneDistance = 60 };

            if (fourteen) {
                world.Children.Add(new AmbientLight(Color.FromRgb(192, 192, 192)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(185, 185, 185), new Vector3D(5, -5.28, 5)));
            } else {
                world.Children.Add(new AmbientLight(Color.FromRgb(99, 99, 106)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(213, 211, 211), new Vector3D(-4, -4, 5)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(129, 134, 141), new Vector3D(5, -2, 4)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(141, 148, 155), new Vector3D(0, -4, -4)));
            }
            Children.Add(new ModelVisual3D { Content = world });
        }

        void SetMaterial(Part part, int index) {
            if (index < 0 || index >= materials.Length) return;
            part.Geometry.Material = materials[index];
            part.Geometry.BackMaterial = surfaces[index].doubleSided ? materials[index] : null;
        }

        static Vector3D Rotate(Quaternion rotation, Vector3D v) {
            var transform = Matrix3D.Identity;
            transform.Rotate(rotation);
            return transform.Transform(v);
        }

        // Applies exact continuous gaze direction (0 deg = up, 90 = right, 180 = down, 270 = left) on top of baseFrame.
        void Aim(Pose[] baseFrame, double lookDeg) {
            for (int k = 0; k < moving.Length; k++) {
                int idx = moving[k];
                gaze[idx] = baseFrame[idx];
            }
            double rad = lookDeg * Math.PI / 180.0;
            double horizontal = Math.Sin(rad);
            double vertical = Math.Cos(rad);

            if (fourteen) {
                int head;
                if (named.TryGetValue("HeadJoint", out head)) {
                    gaze[head].Rotation = new Quaternion(Up, 27 * horizontal) * new Quaternion(Side, -8 * vertical);
                }
                return;
            }

            var turn = new Quaternion(Up, 34 * horizontal) * new Quaternion(Side, -18 * vertical);
            int body = named["Bot_root"], eye = named["EyePivot"];
            gaze[body].Rotation = turn;
            gaze[body].Position = BodyCenter + Rotate(turn, rest[body].Position - BodyCenter);
            var earTurn = new Quaternion(Forward, 6 * horizontal);
            int left = named["EarPivot_1"], right = named["EarPivot1"];
            gaze[left].Rotation = rest[left].Rotation * earTurn;
            gaze[right].Rotation = rest[right].Rotation * earTurn;
            var offset = rest[eye].Position - eyeCenter;
            double radiansToDegrees = 180.0 / (Math.PI * Math.Max(0.2, offset.Length));
            var glance = new Quaternion(Up, 0.06 * horizontal * radiansToDegrees)
                       * new Quaternion(Side, -0.05 * vertical * radiansToDegrees);
            gaze[eye].Position = eyeCenter + Rotate(glance, offset);
            gaze[eye].Rotation = glance * rest[eye].Rotation;
        }

        string lastPoseName = "";
        bool wasLooking;
        double easeUntil;

        // Renders pose & optional blend/look.
        internal void Draw(string poseName, int frame, double blend, double now, double? lookDegrees) {
            Pose[][] clip;
            if (!animations.TryGetValue(poseName, out clip)) clip = animations["idle"];
            int f0 = ((frame % clip.Length) + clip.Length) % clip.Length;
            int f1 = (f0 + 1) % clip.Length;
            Pose[] target = clip[f0];

            if (poseName != lastPoseName || lookDegrees.HasValue || wasLooking) {
                easeUntil = now + 0.35;
            }
            lastPoseName = poseName;
            wasLooking = lookDegrees.HasValue;

            if (lookDegrees.HasValue) {
                Aim(target, lookDegrees.Value);
                target = gaze;
                blend = 0;
            }

            double dt = Calc.Clamp(now - lastUpdate, 0.0, 0.25);
            lastUpdate = now;
            double fraction = (now < easeUntil || blend > 0.001) ? (1.0 - Math.Exp(-15.0 * dt)) : 1.0;
            bool anyChanged = false;

            for (int k = 0; k < moving.Length; k++) {
                int index = moving[k];
                var part = parts[index];
                Pose goal = target[index];
                if (blend > 0.001) goal.Approach(clip[f1][index], blend);

                if (part.Current.Hidden != goal.Hidden) {
                    if (goal.Hidden) part.Parent.Children.Remove(part.Model);
                    else part.Parent.Children.Add(part.Model);
                    anyChanged = true;
                }
                if (part.Geometry != null && part.Current.Material != goal.Material) {
                    SetMaterial(part, goal.Material);
                    anyChanged = true;
                }
                if (part.Current.Approach(goal, fraction)) {
                    var matrix = part.Current.Matrix();
                    if (part.Transform.Matrix != matrix) part.Transform.Matrix = matrix;
                    anyChanged = true;
                }
            }
            Settled = !anyChanged && blend <= 0.001 && now >= easeUntil;
        }

        // Exact 3D mesh raycast when mounted (matching Sources/Model3D.swift hitTest), with analytical fallback when unmounted.
        internal bool HitsNormalized(double u, double vTop) {
            if (u < 0 || u > 1 || vTop < 0 || vTop > 1) return false;
            if (ActualWidth > 1.0 && ActualHeight > 1.0) {
                bool prev = IsHitTestVisible;
                IsHitTestVisible = true;
                try {
                    return VisualTreeHelper.HitTest(this, new Point(u * ActualWidth, vTop * ActualHeight)) != null;
                } finally {
                    IsHitTestVisible = prev;
                }
            }
            if (fourteen) {
                double dxHead = (u - 0.5) / 0.34;
                double dyHead = (vTop - 0.36) / 0.30;
                if (dxHead * dxHead + dyHead * dyHead <= 1.0) return true;
                if (vTop >= 0.35 && vTop <= 0.72 && Math.Abs(u - 0.5) <= 0.32) return true;
                if (vTop > 0.72 && vTop <= 0.99 && Math.Abs(u - 0.5) <= 0.24) return true;
                return false;
            } else {
                double dx = (u - 0.5) / 0.35;
                double dy = (vTop - 0.63) / 0.33;
                if (dx * dx + dy * dy <= 1.0) return true;
                if (vTop >= 0.16 && vTop <= 0.50 && Math.Abs(u - 0.5) <= 0.32) return true;
                return false;
            }
        }
    }
}
