// DshTuanjieBridge.cs
//
// Editor-side half of the DSH Tuanjie bridge. It is a self-contained Editor script
// that polls a request directory and answers into a response directory, so the DSH
// plugin needs no WebSocket handshake, auth token, or third-party package.
//
// Optional dependency: the `com.gamelovers.mcp-unity` package. When that package is
// present its tools are dispatched by name, so this file never links against it and
// still compiles in projects that do not have it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DshTuanjieBridge
{
    [InitializeOnLoad]
    internal static class Bridge
    {
        internal const string DirName = "DshTuanjieBridge";
        private const double PollIntervalSeconds = 0.2;

        private static readonly object FileLock = new object();
        private static double _nextPoll;
        private static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.Ordinal);

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;
        private static string Root => Path.Combine(ProjectRoot, "Library", DirName);
        private static string Requests => Path.Combine(Root, "requests");
        private static string Responses => Path.Combine(Root, "responses");
        private static string StatusFile => Path.Combine(Root, "status.json");

        static Bridge()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EnsureDirectories();
            WriteStatus();
        }

        private static void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(Requests);
                Directory.CreateDirectory(Responses);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DshBridge] Cannot create bridge directories: {ex.Message}");
            }
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollIntervalSeconds;

            if (_nextPoll - Math.Floor(_nextPoll / 5) * 5 < PollIntervalSeconds * 1.5)
            {
                WriteStatus();
            }

            string[] files;
            try
            {
                if (!Directory.Exists(Requests)) EnsureDirectories();
                files = Directory.GetFiles(Requests, "*.json");
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file);
                lock (FileLock)
                {
                    if (!InFlight.Add(id)) continue;
                }
                // Run one command per tick so a long operation cannot stall the editor loop.
                Handle(file, id);
                return;
            }
        }

        private static void Handle(string requestFile, string id)
        {
            object payload = null;
            string error = null;
            try
            {
                string raw = File.ReadAllText(requestFile);
                payload = MiniJson.Parse(raw) as Dictionary<string, object>;
                if (payload == null) throw new InvalidOperationException("Request is not a JSON object.");

                var dict = (Dictionary<string, object>)payload;
                string command = GetString(dict, "command");
                var parameters = dict.TryGetValue("params", out var p) ? p as Dictionary<string, object> : null;
                object result = Dispatch(command, parameters ?? new Dictionary<string, object>());
                var envelope = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["ok"] = true,
                    ["result"] = result,
                    ["respondedAt"] = DateTime.UtcNow.ToString("o"),
                };
                WriteResponse(id, MiniJson.Serialize(envelope));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                var envelope = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["ok"] = false,
                    ["error"] = ex.Message,
                    ["stack"] = ex.StackTrace,
                    ["respondedAt"] = DateTime.UtcNow.ToString("o"),
                };
                try { WriteResponse(id, MiniJson.Serialize(envelope)); }
                catch (Exception writeEx) { Debug.LogError($"[DshBridge] Cannot write error response: {writeEx.Message}"); }
                Debug.LogError($"[DshBridge] '{id}' failed: {error}");
            }
            finally
            {
                try { File.Delete(requestFile); } catch { /* editor may hold a transient lock */ }
                lock (FileLock) { InFlight.Remove(id); }
                WriteStatus();
            }
        }

        private static void WriteResponse(string id, string json)
        {
            Directory.CreateDirectory(Responses);
            string target = Path.Combine(Responses, id + ".json");
            string temp = target + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }

        private static void WriteStatus()
        {
            try
            {
                Directory.CreateDirectory(Root);
                var status = new Dictionary<string, object>
                {
                    ["tickedAt"] = DateTime.UtcNow.ToString("o"),
                    ["playMode"] = EditorApplication.isPlayingOrWillChangePlaymode
                        ? (EditorApplication.isPlaying ? "playing" : "transitioning")
                        : "edit",
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating,
                    ["activeScene"] = SceneManager.GetActiveScene().path,
                    ["activeSceneDirty"] = SceneManager.GetActiveScene().isDirty,
                    ["sceneExtension"] = PreferredSceneExtension(),
                    ["upstreamAvailable"] = UpstreamDispatcher.IsAvailable,
                    ["unityVersion"] = Application.unityVersion,
                };
                string json = MiniJson.Serialize(status);
                string temp = StatusFile + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(StatusFile)) File.Delete(StatusFile);
                File.Move(temp, StatusFile);
            }
            catch { /* status is advisory */ }
        }

        /// <summary>
        /// Tuanjie historically writes `.scene`; this project may use either spelling.
        /// Report what this project actually contains so the Host can pick a suffix.
        /// </summary>
        private static string PreferredSceneExtension()
        {
            try
            {
                string assets = Path.Combine(ProjectRoot, "Assets");
                if (!Directory.Exists(assets)) return ".unity";
                bool sawUnity = Directory.EnumerateFiles(assets, "*.unity", SearchOption.AllDirectories).Any();
                bool sawScene = Directory.EnumerateFiles(assets, "*.scene", SearchOption.AllDirectories).Any();
                if (sawScene && !sawUnity) return ".scene";
            }
            catch { }
            return ".unity";
        }

        private static object Dispatch(string command, Dictionary<string, object> p)
        {
            switch (command)
            {
                case "ping": return Ping();
                case "state": return State();
                case "scene.create": return SceneCreate(p);
                case "scene.load": return SceneLoad(p);
                case "scene.save": return SceneSave(p);
                case "scene.hierarchy": return SceneHierarchy();
                case "object.create": return ObjectCreate(p);
                case "object.find": return ObjectFind(p);
                case "object.setTransform": return ObjectSetTransform(p);
                case "object.addComponent": return ObjectAddComponent(p);
                case "object.setProperty": return ObjectSetProperty(p);
                case "object.delete": return ObjectDelete(p);
                case "visibility.audit": return VisibilityAudit(p);
                case "prefab.create": return PrefabCreate(p);
                case "play.set": return PlaySet(p);
                case "script.write": return ScriptWrite(p);
                case "asset.assignMaterial": return AssignMaterial(p);
                default:
                    // Unknown commands fall through to the optional upstream package.
                    if (UpstreamDispatcher.IsAvailable)
                        return UpstreamDispatcher.Execute(command, p, ProjectRoot);
                    throw new InvalidOperationException($"Unknown command: {command}");
            }
        }

        // ---------------------------------------------------------------- commands

        private static object Ping() => new Dictionary<string, object>
        {
            ["pong"] = true,
            ["unityVersion"] = Application.unityVersion,
            ["projectRoot"] = ProjectRoot,
            ["sceneExtension"] = PreferredSceneExtension(),
        };

        private static object State()
        {
            Scene active = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                ["playMode"] = EditorApplication.isPlaying ? "playing"
                    : EditorApplication.isPlayingOrWillChangePlaymode ? "transitioning" : "edit",
                ["isCompiling"] = EditorApplication.isCompiling,
                ["activeScene"] = active.path,
                ["activeSceneName"] = active.name,
                ["activeSceneDirty"] = active.isDirty,
                ["sceneLoaded"] = active.IsValid(),
                ["rootCount"] = active.IsValid() ? active.GetRootGameObjects().Length : 0,
            };
        }

        private static object SceneCreate(Dictionary<string, object> p)
        {
            string name = GetString(p, "sceneName") ?? throw new InvalidOperationException("sceneName is required.");
            string folder = GetString(p, "folderPath") ?? "Assets/Scenes";
            folder = folder.Replace('\\', '/').TrimEnd('/');
            EnsureFolder(folder);

            string ext = PreferredSceneExtension();
            string scenePath = $"{folder}/{name}{ext}";
            if (File.Exists(Path.Combine(ProjectRoot, scenePath)))
                throw new InvalidOperationException($"A scene already exists at {scenePath}.");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new InvalidOperationException($"Editor refused to save the new scene to {scenePath}.");

            // An empty scene has no camera or light, so the Game view renders black and the
            // result looks broken even though the data is correct. Seed the usual defaults.
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(0f, 1f, -10f);
            camGo.AddComponent<AudioListener>();

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            lightGo.transform.position = new Vector3(0f, 3f, 0f);
            lightGo.transform.eulerAngles = new Vector3(50f, -30f, 0f);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            AssetDatabase.Refresh();
            // Repaint so the Hierarchy/Inspector show the new scene instead of the old frame.
            ActiveEditorTracker.sharedTracker.ForceRebuild();
            SceneView.RepaintAll();
            EditorApplication.RepaintHierarchyWindow();
            return new Dictionary<string, object>
            {
                ["scenePath"] = scenePath,
                ["created"] = true,
                ["seededCamera"] = true,
                ["seededLight"] = true,
            };
        }

        private static object SceneLoad(Dictionary<string, object> p)
        {
            string scenePath = GetString(p, "scenePath") ?? throw new InvalidOperationException("scenePath is required.");
            scenePath = ResolveScenePath(scenePath);
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            return new Dictionary<string, object> { ["scenePath"] = scene.path, ["name"] = scene.name };
        }

        private static object SceneSave(Dictionary<string, object> p)
        {
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid()) throw new InvalidOperationException("There is no valid active scene to save.");
            string target = GetString(p, "saveAsPath");
            bool ok = string.IsNullOrEmpty(target)
                ? EditorSceneManager.SaveScene(active)
                : EditorSceneManager.SaveScene(active, ResolveScenePath(target, allowMissingAsset: true));
            if (!ok) throw new InvalidOperationException("Editor refused to save the scene.");
            AssetDatabase.Refresh();
            return new Dictionary<string, object> { ["saved"] = true, ["scenePath"] = SceneManager.GetActiveScene().path };
        }

        private static object SceneHierarchy()
        {
            Scene active = SceneManager.GetActiveScene();
            var roots = new List<object>();
            foreach (GameObject go in active.GetRootGameObjects())
                roots.Add(Describe(go, 0, 3, false));
            return new Dictionary<string, object>
            {
                ["scenePath"] = active.path,
                ["name"] = active.name,
                ["isDirty"] = active.isDirty,
                ["roots"] = roots,
            };
        }

        private static object ObjectCreate(Dictionary<string, object> p)
        {
            string name = GetString(p, "name") ?? "GameObject";
            string parentPath = GetString(p, "parentPath");
            string primitive = GetString(p, "primitiveType");
            bool withRenderer = GetBool(p, "withRenderer", true);

            GameObject go;
            if (!string.IsNullOrEmpty(primitive))
            {
                var type = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitive, true);
                go = GameObject.CreatePrimitive(type);
                go.name = name;
            }
            else
            {
                go = new GameObject(name);
                if (withRenderer)
                {
                    // A bare GameObject with only a MeshFilter is invisible in the scene.
                    // Attach renderer + a real mesh so "created" actually shows up.
                    var filter = go.AddComponent<MeshFilter>();
                    var renderer = go.AddComponent<MeshRenderer>();
                    filter.sharedMesh = BuiltinMesh();
                    renderer.sharedMaterial = BuiltinMaterial();
                }
            }

            if (!string.IsNullOrEmpty(parentPath))
            {
                GameObject parent = FindByPath(parentPath);
                if (parent == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    throw new InvalidOperationException($"Parent '{parentPath}' was not found.");
                }
                go.transform.SetParent(parent.transform, false);
            }

            var position = GetVector3(p, "position");
            var rotation = GetVector3(p, "rotation");
            var scale = GetVector3(p, "scale");
            if (position.HasValue) go.transform.position = position.Value;
            if (rotation.HasValue) go.transform.eulerAngles = rotation.Value;
            if (scale.HasValue) go.transform.localScale = scale.Value;

            MarkSceneDirty();
            return Describe(go, 0, 0, true);
        }

        private static object ObjectFind(Dictionary<string, object> p)
        {
            string target = GetString(p, "path");
            GameObject go = FindByPath(target);
            if (go == null) throw new InvalidOperationException($"GameObject '{target}' was not found.");
            return Describe(go, 0, GetInt(p, "maxDepth", 1), GetBool(p, "withProperties", false));
        }

        private static object ObjectSetTransform(Dictionary<string, object> p)
        {
            GameObject go = FindByPath(GetString(p, "path"))
                ?? throw new InvalidOperationException($"GameObject '{GetString(p, "path")}' was not found.");
            string space = GetString(p, "space") ?? "world";
            var position = GetVector3(p, "position");
            var rotation = GetVector3(p, "rotation");
            var scale = GetVector3(p, "scale");
            if (position.HasValue)
            {
                if (space == "local") go.transform.localPosition = position.Value;
                else go.transform.position = position.Value;
            }
            if (rotation.HasValue)
            {
                if (space == "local") go.transform.localEulerAngles = rotation.Value;
                else go.transform.eulerAngles = rotation.Value;
            }
            if (scale.HasValue) go.transform.localScale = scale.Value;
            MarkSceneDirty();
            // Always read back: an acknowledgement alone does not prove the transform landed.
            return Describe(go, 0, 0, true);
        }

        private static object ObjectAddComponent(Dictionary<string, object> p)
        {
            GameObject go = FindByPath(GetString(p, "path"))
                ?? throw new InvalidOperationException($"GameObject '{GetString(p, "path")}' was not found.");
            string typeName = GetString(p, "componentType")
                ?? throw new InvalidOperationException("componentType is required.");

            Type type = FindType(typeName);
            if (type == null)
            {
                AssetDatabase.Refresh();
                type = FindType(typeName);
            }
            if (type == null)
                throw new InvalidOperationException(
                    $"Component type '{typeName}' was not found. If you just wrote the script, wait for compilation first.");

            Component existing = go.GetComponent(type);
            if (existing == null) existing = go.AddComponent(type);
            MarkSceneDirty();
            return Describe(go, 0, 0, true);
        }

        private static object ObjectSetProperty(Dictionary<string, object> p)
        {
            GameObject go = FindByPath(GetString(p, "path"))
                ?? throw new InvalidOperationException($"GameObject '{GetString(p, "path")}' was not found.");
            string componentType = GetString(p, "componentType") ?? throw new InvalidOperationException("componentType is required.");
            var values = p.TryGetValue("values", out var raw) ? raw as Dictionary<string, object> : null;
            if (values == null) throw new InvalidOperationException("values is required.");

            Type type = FindType(componentType) ?? throw new InvalidOperationException($"Component type '{componentType}' was not found.");
            Component component = go.GetComponent(type) ?? go.AddComponent(type);

            var so = new SerializedObject(component);
            foreach (var pair in values)
            {
                SerializedProperty prop = so.FindProperty(pair.Key);
                if (prop == null) throw new InvalidOperationException($"Field '{pair.Key}' was not found on {type.Name}.");
                ApplyValue(prop, pair.Value);
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            MarkSceneDirty();
            return Describe(go, 0, 0, true);
        }

        private static object ObjectDelete(Dictionary<string, object> p)
        {
            GameObject go = FindByPath(GetString(p, "path"))
                ?? throw new InvalidOperationException($"GameObject '{GetString(p, "path")}' was not found.");
            string path = PathOf(go);
            UnityEngine.Object.DestroyImmediate(go);
            MarkSceneDirty();
            return new Dictionary<string, object> { ["deleted"] = path };
        }

        /// <summary>
        /// Report every object that would be invisible: no Renderer, or a Renderer with
        /// no mesh. This is the check that a plain "component added" acknowledgement misses.
        /// </summary>
        private static object VisibilityAudit(Dictionary<string, object> p)
        {
            Scene active = SceneManager.GetActiveScene();
            var visible = new List<object>();
            var invisible = new List<object>();
            foreach (GameObject root in active.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    var entry = new Dictionary<string, object> { ["path"] = PathOf(t.gameObject) };
                    var renderer = t.GetComponent<Renderer>();
                    var filter = t.GetComponent<MeshFilter>();
                    entry["hasRenderer"] = renderer != null;
                    entry["hasMesh"] = filter != null && filter.sharedMesh != null;
                    if (renderer == null || (filter != null && filter.sharedMesh == null))
                    {
                        bool isUi = t.GetComponent<CanvasRenderer>() != null;
                        bool isLightOrCamera = t.GetComponent<Light>() != null || t.GetComponent<Camera>() != null;
                        if (!isUi && !isLightOrCamera) invisible.Add(entry);
                    }
                    else visible.Add(entry);
                }
            }
            return new Dictionary<string, object>
            {
                ["visibleCount"] = visible.Count,
                ["invisibleCount"] = invisible.Count,
                ["invisible"] = invisible,
            };
        }

        private static object PrefabCreate(Dictionary<string, object> p)
        {
            string prefabName = GetString(p, "prefabName") ?? throw new InvalidOperationException("prefabName is required.");
            string folder = GetString(p, "folderPath") ?? "Assets/Prefabs";
            folder = folder.Replace('\\', '/').TrimEnd('/');
            // A prefab outside Assets/ is not an asset: Unity silently ignores it.
            if (!folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"folderPath must start with 'Assets/', got '{folder}'.");
            EnsureFolder(folder);

            var holder = new GameObject(prefabName);
            try
            {
                var position = GetVector3(p, "position");
                if (position.HasValue) holder.transform.position = position.Value;

                string componentType = GetString(p, "componentType");
                if (!string.IsNullOrEmpty(componentType))
                {
                    Type type = FindType(componentType)
                        ?? throw new InvalidOperationException($"Component type '{componentType}' was not found.");
                    Component component = holder.AddComponent(type);
                    if (p.TryGetValue("fieldValues", out var raw) && raw is Dictionary<string, object> fieldValues)
                    {
                        var so = new SerializedObject(component);
                        foreach (var pair in fieldValues)
                        {
                            SerializedProperty prop = so.FindProperty(pair.Key);
                            if (prop == null) continue;
                            ApplyValue(prop, pair.Value);
                        }
                        so.ApplyModifiedProperties();
                    }
                }

                string fullPath = $"{folder}/{prefabName}.prefab";
                string absolute = Path.Combine(ProjectRoot, fullPath);
                PrefabUtility.SaveAsPrefabAsset(holder, fullPath);
                AssetDatabase.Refresh();

                // Assert the asset really exists; do not trust the Save call alone.
                if (!File.Exists(absolute))
                    throw new InvalidOperationException($"Prefab was not written to {fullPath}.");

                return new Dictionary<string, object>
                {
                    ["prefabPath"] = fullPath,
                    ["existsOnDisk"] = true,
                    ["created"] = true,
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        private static object PlaySet(Dictionary<string, object> p)
        {
            string action = GetString(p, "action") ?? "status";
            switch (action)
            {
                case "play":
                    if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
                    break;
                case "stop":
                    if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
                    break;
                case "pause":
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    break;
                case "status":
                    break;
                default:
                    throw new InvalidOperationException($"Unknown play action: {action}");
            }
            return new Dictionary<string, object>
            {
                ["requested"] = action,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPlayingOrWillChange"] = EditorApplication.isPlayingOrWillChangePlaymode,
                ["isPaused"] = EditorApplication.isPaused,
            };
        }

        private static object ScriptWrite(Dictionary<string, object> p)
        {
            string path = GetString(p, "path") ?? throw new InvalidOperationException("path is required.");
            string content = GetString(p, "content") ?? throw new InvalidOperationException("content is required.");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Script path must start with 'Assets/', got '{path}'.");
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only .cs scripts are supported.");

            string absolute = Path.Combine(ProjectRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, content, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return new Dictionary<string, object> { ["path"] = path, ["written"] = true };
        }

        private static object AssignMaterial(Dictionary<string, object> p)
        {
            string objectPath = GetString(p, "path") ?? throw new InvalidOperationException("path is required.");
            string materialPath = GetString(p, "materialPath") ?? throw new InvalidOperationException("materialPath is required.");
            GameObject go = FindByPath(objectPath) ?? throw new InvalidOperationException($"GameObject '{objectPath}' was not found.");
            var renderer = go.GetComponent<Renderer>() ?? throw new InvalidOperationException($"'{objectPath}' has no Renderer.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath)
                ?? throw new InvalidOperationException($"Material '{materialPath}' was not found.");
            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
            MarkSceneDirty();
            return new Dictionary<string, object>
            {
                ["path"] = objectPath,
                ["material"] = materialPath,
                ["shader"] = material.shader != null ? material.shader.name : null,
            };
        }

        // ------------------------------------------------------------------- utils

        private static void MarkSceneDirty()
        {
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid()) EditorSceneManager.MarkSceneDirty(active);
            // External edits do not repaint the Hierarchy by themselves, so a stale panel
            // would show the previous scene and look like the write failed.
            EditorApplication.RepaintHierarchyWindow();
            SceneView.RepaintAll();
        }

        private static string ResolveScenePath(string scenePath, bool allowMissingAsset = false)
        {
            scenePath = scenePath.Replace('\\', '/');
            if (Path.HasExtension(scenePath) && allowMissingAsset) return scenePath;
            string absolute = Path.Combine(ProjectRoot, scenePath);
            if (File.Exists(absolute)) return scenePath;
            // Accept a bare name and try both suffixes this engine family uses.
            foreach (string candidate in new[] { scenePath + ".unity", scenePath + ".scene" })
            {
                if (File.Exists(Path.Combine(ProjectRoot, candidate))) return candidate;
            }
            if (allowMissingAsset) return scenePath;
            throw new InvalidOperationException(
                $"Scene '{scenePath}' was not found (looked for .unity and .scene).");
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static Mesh BuiltinMesh()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
            return mesh;
        }

        private static Material BuiltinMaterial()
        {
            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            return shader != null ? new Material(shader) : null;
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid())
            {
                foreach (GameObject root in active.GetRootGameObjects())
                {
                    if (root.name == path) return root;
                    Transform found = root.transform.Find(path);
                    if (found != null) return found.gameObject;
                }
            }
            return GameObject.Find(path);
        }

        private static string PathOf(GameObject go)
        {
            var parts = new List<string>();
            for (Transform t = go.transform; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        private static Type FindType(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (Type t in types)
                {
                    if (t == null || t.FullName == null) continue;
                    if (t.FullName == name || t.Name == name) return t;
                }
            }
            return null;
        }

        private static object Describe(GameObject go, int depth, int maxDepth, bool withProperties)
        {
            var components = new List<object>();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    components.Add(new Dictionary<string, object> { ["type"] = "<missing script>" });
                    continue;
                }
                var entry = new Dictionary<string, object> { ["type"] = c.GetType().Name };
                if (withProperties)
                {
                    var props = new Dictionary<string, object>();
                    try
                    {
                        var so = new SerializedObject(c);
                        SerializedProperty it = so.GetIterator();
                        int guard = 0;
                        while (it.NextVisible(true) && guard++ < 400)
                        {
                            if (it.propertyType == SerializedPropertyType.ObjectReference ||
                                it.propertyType == SerializedPropertyType.Generic) continue;
                            props[it.propertyPath] = ReadProperty(it);
                        }
                    }
                    catch { }
                    entry["values"] = props;
                }
                components.Add(entry);
            }

            var result = new Dictionary<string, object>
            {
                ["path"] = PathOf(go),
                ["name"] = go.name,
                ["activeSelf"] = go.activeSelf,
                ["tag"] = go.tag,
                ["layer"] = go.layer,
                ["components"] = components,
                ["transform"] = new Dictionary<string, object>
                {
                    ["position"] = Vec(go.transform.position),
                    ["localPosition"] = Vec(go.transform.localPosition),
                    ["eulerAngles"] = Vec(go.transform.eulerAngles),
                    ["localScale"] = Vec(go.transform.localScale),
                },
                ["hasRenderer"] = go.GetComponent<Renderer>() != null,
            };

            var filter = go.GetComponent<MeshFilter>();
            result["hasMesh"] = filter != null && filter.sharedMesh != null;

            if (depth < maxDepth)
            {
                var children = new List<object>();
                foreach (Transform child in go.transform)
                    children.Add(Describe(child.gameObject, depth + 1, maxDepth, false));
                result["children"] = children;
            }
            return result;
        }

        private static object ReadProperty(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask: return prop.intValue;
                case SerializedPropertyType.Boolean: return prop.boolValue;
                case SerializedPropertyType.Float: return prop.floatValue;
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Color:
                    Color c = prop.colorValue;
                    return new Dictionary<string, object> { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case SerializedPropertyType.Enum: return prop.enumValueIndex;
                case SerializedPropertyType.Vector3: return Vec(prop.vector3Value);
                case SerializedPropertyType.Vector2: return new Dictionary<string, object> { ["x"] = prop.vector2Value.x, ["y"] = prop.vector2Value.y };
                default: return prop.propertyType.ToString();
            }
        }

        private static void ApplyValue(SerializedProperty prop, object value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask: prop.intValue = Convert.ToInt32(value); break;
                case SerializedPropertyType.Boolean: prop.boolValue = Convert.ToBoolean(value); break;
                case SerializedPropertyType.Float: prop.floatValue = Convert.ToSingle(value); break;
                case SerializedPropertyType.String: prop.stringValue = Convert.ToString(value); break;
                case SerializedPropertyType.Enum:
                    if (value is string enumName) prop.enumValueIndex = Array.IndexOf(prop.enumNames, enumName);
                    else prop.enumValueIndex = Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Color:
                    if (value is Dictionary<string, object> c)
                        prop.colorValue = new Color(F(c, "r"), F(c, "g"), F(c, "b"), c.ContainsKey("a") ? F(c, "a") : 1f);
                    break;
                case SerializedPropertyType.Vector3:
                    if (value is Dictionary<string, object> v)
                        prop.vector3Value = new Vector3(F(v, "x"), F(v, "y"), F(v, "z"));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported field type {prop.propertyType} for '{prop.propertyPath}'.");
            }
        }

        private static float F(Dictionary<string, object> d, string key) => d.ContainsKey(key) ? Convert.ToSingle(d[key]) : 0f;

        private static Dictionary<string, object> Vec(Vector3 v) =>
            new Dictionary<string, object> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        private static string GetString(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static bool GetBool(Dictionary<string, object> d, string key, bool fallback) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToBoolean(v) : fallback;

        private static int GetInt(Dictionary<string, object> d, string key, int fallback) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v) : fallback;

        private static Vector3? GetVector3(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var raw)) return null;
            if (raw is Dictionary<string, object> v)
                return new Vector3(F(v, "x"), F(v, "y"), F(v, "z"));
            return null;
        }
    }

    /// <summary>
    /// Optional pass-through to the com.gamelovers.mcp-unity tools when that package is
    /// installed. Reflection keeps this file compilable without the package.
    /// </summary>
    internal static class UpstreamDispatcher
    {
        private static Type _serverType;

        internal static bool IsAvailable
        {
            get
            {
                if (_serverType == null) _serverType = FindServerType();
                return _serverType != null;
            }
        }

        private static Type FindServerType()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (Type t in types)
                    if (t != null && t.FullName == "McpUnity.Unity.McpUnityServer") return t;
            }
            return null;
        }

        internal static object Execute(string command, Dictionary<string, object> parameters, string projectRoot)
        {
            Type serverType = _serverType ?? FindServerType();
            if (serverType == null) throw new InvalidOperationException($"Unknown command: {command}");

            PropertyInfo instanceProp = serverType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object server = instanceProp?.GetValue(null);
            if (server == null) throw new InvalidOperationException("The upstream MCP Unity server is not initialised.");

            MethodInfo tryGet = serverType.GetMethod("TryGetTool",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (tryGet == null) throw new InvalidOperationException("Upstream server exposes no TryGetTool.");

            object[] args = { command, null };
            bool found = (bool)tryGet.Invoke(server, args)!;
            if (!found) throw new InvalidOperationException($"Unknown command: {command}");

            object tool = args[1];

            // Build the JObject through reflection: a plain project has no Newtonsoft
            // reference, and a compile-time dependency here would break the whole bridge.
            object jo = JTokenFactory.CreateObject(parameters, out bool handled);
            if (!handled)
                throw new InvalidOperationException(
                    "The upstream MCP Unity package is present but its JSON assembly could not be located.");

            MethodInfo execute = tool.GetType().GetMethod("Execute");
            object result = execute!.Invoke(tool, new object[] { jo });
            return JTokenFactory.ToDictionary(result);
        }
    }

    /// <summary>
    /// Converts between plain CLR values and the JSON types used by the optional upstream
    /// package, entirely through reflection so this assembly never links against it.
    /// </summary>
    internal static class JTokenFactory
    {
        private static Type _jObjectType;
        private static Type _jTokenType;
        private static bool _searched;

        private static void Resolve()
        {
            if (_searched) return;
            _searched = true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type objectType = null;
                try { objectType = asm.GetType("Newtonsoft.Json.Linq.JObject", false); }
                catch { continue; }
                if (objectType == null) continue;
                _jObjectType = objectType;
                _jTokenType = asm.GetType("Newtonsoft.Json.Linq.JToken", false);
                return;
            }
        }

        internal static object CreateObject(Dictionary<string, object> parameters, out bool handled)
        {
            handled = false;
            Resolve();
            if (_jObjectType == null || _jTokenType == null) return null;

            object jObject = Activator.CreateInstance(_jObjectType);
            MethodInfo fromObject = _jTokenType.GetMethod("FromObject", new[] { typeof(object) });
            PropertyInfo item = _jObjectType.GetProperty("Item", new[] { typeof(string) });
            if (fromObject == null || item == null) return null;

            foreach (var pair in parameters)
            {
                object token = fromObject.Invoke(null, new[] { pair.Value ?? (object)string.Empty });
                item.SetValue(jObject, token);
            }
            handled = true;
            return jObject;
        }

        internal static Dictionary<string, object> ToDictionary(object jToken)
        {
            if (jToken == null) return new Dictionary<string, object>();
            Resolve();
            if (_jTokenType == null) return new Dictionary<string, object>();
            MethodInfo toObject = _jTokenType.GetMethod("ToObject", new[] { typeof(Type) });
            if (toObject == null) return new Dictionary<string, object>();
            object converted = toObject.Invoke(jToken, new object[] { typeof(object) });
            return converted as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
    }

    /// <summary>Minimal JSON reader/writer so the bridge has no external dependency.</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            int index = 0;
            object value = ParseValue(json, ref index);
            SkipWhitespace(json, ref index);
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (s[i] != ':') throw new FormatException("Expected ':' in JSON object.");
                i++;
                result[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated JSON object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}' in JSON object.");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated JSON array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']' in JSON array.");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected a JSON string.");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(esc); break;
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "-+.eE0123456789".IndexOf(s[i]) >= 0) i++;
            string token = s.Substring(start, i - start);
            if (token.IndexOf('.') >= 0 || token.IndexOf('e') >= 0 || token.IndexOf('E') >= 0)
                return double.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
            return long.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException($"Expected '{literal}' in JSON.");
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(value, sb);
            return sb.ToString();
        }

        private static void Write(object value, StringBuilder sb)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(s, sb); break;
                case float f: sb.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                case int or long or short or byte: sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool firstEntry = true;
                    foreach (var pair in dict)
                    {
                        if (!firstEntry) sb.Append(',');
                        firstEntry = false;
                        WriteString(pair.Key, sb);
                        sb.Append(':');
                        Write(pair.Value, sb);
                    }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(item, sb);
                    }
                    sb.Append(']');
                    break;
                default: WriteString(value.ToString(), sb); break;
            }
        }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
