using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;

namespace Gloria {
    // The rig stores local transforms in the original SceneKit hierarchy. Static meshes
    // are shared, including the 25 identical keyboard keys. Only moving nodes are updated.
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
            public Dictionary<string,double[][][]> animations { get; set; }
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
                Rotation = new Quaternion(values[offset+3], values[offset+4], values[offset+5], values[offset+6]);
                Rotation.Normalize();
                Scale = Vector(values, offset+7);
                Hidden = values[offset+10] != 0;
                Material = (int)values[offset+11];
            }
            internal Matrix3D Matrix() {
                var matrix = Matrix3D.Identity;
                matrix.Scale(Scale);
                matrix.Rotate(Rotation);
                matrix.Translate(Position);
                return matrix;
            }
            internal void Approach(Pose target, double fraction) {
                // Settle exactly on the target so a stationary gaze stops invalidating the viewport.
                var delta = target.Position - Position;
                Position = delta.LengthSquared < 1e-10 ? target.Position : Position + delta * fraction;
                delta = target.Scale - Scale;
                Scale = delta.LengthSquared < 1e-10 ? target.Scale : Scale + delta * fraction;
                double dot = Rotation.X*target.Rotation.X + Rotation.Y*target.Rotation.Y
                           + Rotation.Z*target.Rotation.Z + Rotation.W*target.Rotation.W;
                Rotation = Math.Abs(dot) > 0.99999999 ? target.Rotation : Quaternion.Slerp(Rotation,target.Rotation,fraction);
                Hidden = target.Hidden;
                Material = target.Material;
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
        readonly Dictionary<string,Pose[][]> animations = new Dictionary<string,Pose[][]>();
        readonly Dictionary<string,int> named = new Dictionary<string,int>();
        readonly Vector3D eyeCenter;
        readonly Pose[] gaze;
        DateTime lastUpdate;
        static readonly Vector3D BodyCenter = new Vector3D(0, 0, 1.17);
        static readonly Vector3D Up = new Vector3D(0, 0, 1);
        static readonly Vector3D Side = new Vector3D(1, 0, 0);
        static readonly Vector3D Forward = new Vector3D(0, 1, 0);

        static Vector3D Vector(double[] values, int index) {
            return new Vector3D(values[index],values[index+1],values[index+2]);
        }
        static MeshGeometry3D[] LoadMeshes(string file, int count, bool textured) {
            using (var reader = new BinaryReader(File.OpenRead(file))) {
                if (new string(reader.ReadChars(4)) != (textured ? "PET2" : "GRK1")) throw new InvalidDataException("Invalid model mesh format");
                var meshes = new MeshGeometry3D[count];
                for (int i=0; i<count; i++) {
                    int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                    var positions = new Point3DCollection(vertices);
                    var normals = new Vector3DCollection(vertices);
                    var triangles = new Int32Collection(indices);
                    var uv = textured ? new PointCollection(vertices) : null;
                    for (int v=0; v<vertices; v++) {
                        positions.Add(new Point3D(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
                        normals.Add(new Vector3D(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
                        if (textured) uv.Add(new Point(reader.ReadSingle(),reader.ReadSingle()));
                    }
                    for (int j=0; j<indices; j++) triangles.Add(reader.ReadInt32());
                    var mesh = new MeshGeometry3D { Positions=positions, Normals=normals, TriangleIndices=triangles };
                    if (textured) mesh.TextureCoordinates = uv;
                    mesh.Freeze();
                    meshes[i] = mesh;
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Invalid model mesh length");
                return meshes;
            }
        }
        static SolidColorBrush Brush(double[] rgb,double alpha) {
            return new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha*255), (byte)Math.Round(rgb[0]*255),
                (byte)Math.Round(rgb[1]*255), (byte)Math.Round(rgb[2]*255)));
        }
        static Material MakeMaterial(Surface surface, string directory) {
            var result = new MaterialGroup();
            Brush diffuse = Brush(surface.diffuse,surface.alpha);
            if (!String.IsNullOrEmpty(surface.texture)) {
                var image = new BitmapImage(new Uri(Path.Combine(directory,surface.texture)));
                diffuse = new ImageBrush(image) { ViewportUnits=BrushMappingMode.Absolute, Viewport=new Rect(0,0,1,1), TileMode=TileMode.Tile };
            }
            if (surface.unlit) {
                // DiffuseMaterial supplies the alpha to WPF's emissive pass.
                result.Children.Add(new DiffuseMaterial(Brush(new double[]{0,0,0},surface.alpha)));
                result.Children.Add(new EmissiveMaterial(diffuse));
            } else {
                result.Children.Add(new DiffuseMaterial(diffuse));
                var e = surface.emission;
                if (e[0]+e[1]+e[2] > 0) result.Children.Add(new EmissiveMaterial(Brush(e,surface.alpha)));
                if (surface.name == "Core_GlossBlack") result.Children.Add(new SpecularMaterial(Brushes.DimGray,62));
            }
            result.Freeze();
            return result;
        }
        internal ModelScene(Character character, string id) {
            art = character;
            string directory = art.DirectoryPath;
            fourteen = id == "fourteen";
            var json = new JavaScriptSerializer { MaxJsonLength=8*1024*1024 };
            var rig = json.Deserialize<Rig>(File.ReadAllText(Path.Combine(directory,id+".rig.json")));
            if (rig.format != (fourteen ? 2 : 1)) throw new InvalidDataException("Invalid model rig format");
            var meshes = LoadMeshes(Path.Combine(directory,id+".mesh"),rig.meshCount,fourteen);
            surfaces = rig.materials;
            materials = Array.ConvertAll(surfaces,s => MakeMaterial(s,directory));
            eyeCenter = Vector(rig.eyeCenter,0);
            var world = new Model3DGroup();
            parts = new Part[rig.nodes.Length];
            rest = new Pose[parts.Length];
            for (int i=0; i<parts.Length; i++) {
                var node = rig.nodes[i];
                var part = parts[i] = new Part();
                if (!String.IsNullOrEmpty(node.name) && !named.ContainsKey(node.name)) named.Add(node.name,i);
                rest[i] = part.Current = new Pose(node.rest,0);
                part.Transform.Matrix = part.Current.Matrix();
                part.Model.Transform = part.Transform;
                part.Parent = node.parent < 0 ? world : parts[node.parent].Model;
                if (node.mesh >= 0) {
                    part.Geometry = new GeometryModel3D { Geometry=meshes[node.mesh] };
                    SetMaterial(part,part.Current.Material);
                    part.Model.Children.Add(part.Geometry);
                }
                if (!part.Current.Hidden) part.Parent.Children.Add(part.Model);
            }
            var dynamicParts = new HashSet<int>();
            foreach (var clip in rig.animations) {
                var frames = new Pose[clip.Value.Length][];
                for (int i=0; i<frames.Length; i++) {
                    frames[i] = (Pose[])rest.Clone();
                    foreach (var row in clip.Value[i]) {
                        int index = (int)row[0];
                        dynamicParts.Add(index);
                        frames[i][index] = new Pose(row,1);
                    }
                }
                animations.Add(clip.Key,frames);
            }
            foreach (var name in (fourteen ? new[]{"HeadJoint"} : new[]{"Bot_root","EarPivot_1","EarPivot1","EyePivot"})) dynamicParts.Add(named[name]);
            moving = new int[dynamicParts.Count];
            dynamicParts.CopyTo(moving);
            gaze = (Pose[])rest.Clone();
            Camera = new OrthographicCamera((Point3D)Vector(rig.cameraPosition,0),Vector(rig.cameraFront,0),
                Vector(rig.cameraUp,0),rig.cameraWidth) { NearPlaneDistance=0.1, FarPlaneDistance=60 };
            if (fourteen) {
                world.Children.Add(new AmbientLight(Color.FromRgb(192,192,192)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(185,185,185), new Vector3D(5,-5.28,5)));
            } else {
                world.Children.Add(new AmbientLight(Color.FromRgb(99,99,106)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(213,211,211),new Vector3D(-4,-4,5)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(129,134,141),new Vector3D(5,-2,4)));
                world.Children.Add(new DirectionalLight(Color.FromRgb(141,148,155),new Vector3D(0,-4,-4)));
            }
            Children.Add(new ModelVisual3D { Content=world });
            if (fourteen) {
                var motion=new TransformGroup();
                motion.Children.Add(weight);motion.Children.Add(lean);motion.Children.Add(lift);
                RenderTransform=motion;
                RenderTransformOrigin=new Point(.5,art.FeetY/art.H);
            }
            VerticalAlignment = VerticalAlignment.Bottom;
            ClipToBounds = false;
        }
        void SetMaterial(Part part, int index) {
            part.Geometry.Material = materials[index];
            part.Geometry.BackMaterial = surfaces[index].doubleSided ? materials[index] : null;
        }
        static Vector3D Rotate(Quaternion rotation, Vector3D v) {
            var transform = Matrix3D.Identity;
            transform.Rotate(rotation);
            return transform.Transform(v);
        }
        // Use the same continuous yaw, pitch and curved-visor eye motion as GrokPoses.
        // The rig uses Blender axes inside Bot_root: Z up, -Y toward the viewer.
        void Aim(double horizontal, double vertical) {
            if (fourteen) {
                gaze[named["HeadJoint"]].Rotation = new Quaternion(Up,27*horizontal) * new Quaternion(Side,-8*vertical);
                return;
            }
            var turn = new Quaternion(Up,34*horizontal) * new Quaternion(Side,-18*vertical);
            int body = named["Bot_root"], eye = named["EyePivot"];
            gaze[body].Rotation = turn;
            gaze[body].Position = BodyCenter + Rotate(turn,rest[body].Position-BodyCenter);
            var earTurn = new Quaternion(Forward,6*horizontal);
            int left = named["EarPivot_1"], right = named["EarPivot1"];
            gaze[left].Rotation = rest[left].Rotation * earTurn;
            gaze[right].Rotation = rest[right].Rotation * earTurn;
            var offset = rest[eye].Position-eyeCenter;
            var radiansToDegrees = 180/(Math.PI*Math.Max(0.2,offset.Length));
            var glance = new Quaternion(Up,0.06*horizontal*radiansToDegrees)
                       * new Quaternion(Side,-0.05*vertical*radiansToDegrees);
            gaze[eye].Position = eyeCenter+Rotate(glance,offset);
            gaze[eye].Rotation = glance*rest[eye].Rotation;
        }
        bool looking;
        string continuousPose;
        DateTime clipStart;
        readonly ScaleTransform weight=new ScaleTransform();
        readonly RotateTransform lean=new RotateTransform();
        readonly TranslateTransform lift=new TranslateTransform();
        internal double CarryVelocity {get;set;}
        internal bool InFlight {get;set;}

        // Sample adjacent rig poses on the animation clock. A late tick must not
        // extend the beat or hold the limbs at a key pose for an extra frame.
        internal void Draw(string pose,int frame,DateTime now,double? peekAngle) {
            var clip=animations[pose];
            bool continuous=fourteen && (pose=="dance" || pose=="dangle");
            if (pose!=continuousPose) {
                continuousPose=pose;clipStart=now;
                if (fourteen) RenderTransformOrigin=new Point(.5,pose=="dangle" ? art.FaceY/art.H : art.FeetY/art.H);
                lean.Angle=lift.Y=0;weight.ScaleX=weight.ScaleY=1;
            }
            double blend=0;
            if (continuous) {
                double t=Math.Max(0,(now-clipStart).TotalSeconds);
                double keySeconds=pose=="dangle" && InFlight ? .09 : art.Get(pose).Times[0]/1000.0;
                double phase=t/keySeconds;
                frame=(int)(phase%clip.Length);
                blend=phase-Math.Floor(phase);
                if (pose=="dance") {
                    double beats=2*phase/clip.Length;
                    double within=beats-Math.Floor(beats);
                    double hit=Math.Max(0,1-5*within);
                    lean.Angle=3*Math.Cos(Math.PI*beats);
                    lift.Y=-3*(ActualHeight/art.H/.62)*Math.Sin(Math.PI*within);
                    weight.ScaleX=1+.04*hit;weight.ScaleY=1-.06*hit;
                } else {
                    double angle=InFlight ?
                        -Math.Max(-32,Math.Min(32,CarryVelocity*.014))+Math.Sin(t*16)*Math.Min(8,Math.Abs(CarryVelocity)/120) :
                        -Math.Max(-30,Math.Min(30,CarryVelocity*.02))+4*Math.Sin(t*4.2);
                    lean.Angle=angle;
                }
            }
            Pose[] target=clip[frame];
            if (peekAngle.HasValue) {
                Aim(Math.Sin(peekAngle.Value),Math.Cos(peekAngle.Value)); target=gaze;looking=true;
            } else if ((pose=="idle" || pose=="perch") && IsLoaded && ActualWidth>0) {
                var cursor = System.Windows.Forms.Cursor.Position;
                Point local = PointFromScreen(new Point(cursor.X,cursor.Y));
                // The visor centre sits about two thirds of the way down the original cell.
                var dx = local.X-ActualWidth*(fourteen ? art.FaceX/art.W : 0.5);
                var dy = ActualHeight*(fourteen ? art.FaceY/art.H : 0.66)-local.Y;
                var distance = Math.Sqrt(dx*dx+dy*dy);
                double near = ActualWidth*0.19, far = ActualWidth*2.5+120;
                double margin = looking ? 12 : 0;
                looking = distance > Math.Max(1,near-margin) && distance < far+margin;
                if (looking) { Aim(dx/distance,dy/distance); target=gaze; }
            } else looking = false;
            double dt = (now-lastUpdate).TotalSeconds;
            lastUpdate = now;
            double fraction = 1-Math.Exp(-14*Math.Max(0,Math.Min(1,dt)));
            foreach (int index in moving) {
                var part = parts[index];
                Pose goal = target[index];
                if (continuous) goal.Approach(clip[(frame+1)%clip.Length][index],blend);
                if (part.Current.Hidden != goal.Hidden) {
                    if (goal.Hidden) part.Parent.Children.Remove(part.Model);
                    else part.Parent.Children.Add(part.Model);
                }
                if (part.Geometry!=null && part.Current.Material!=goal.Material) SetMaterial(part,goal.Material);
                part.Current.Approach(goal,fraction);
                var matrix = part.Current.Matrix();
                if (part.Transform.Matrix != matrix) part.Transform.Matrix = matrix;
            }
        }
    }
}
