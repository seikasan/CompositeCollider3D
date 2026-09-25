using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Composite = CompositeCollider3D.CompositeCollider3D;
using Source = CompositeCollider3D.CompositeColliderSource3D;

namespace CompositeCollider3D.Editor
{
    [CustomEditor(typeof(Composite))]
    public sealed class CompositeCollider3DEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sources"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_generationType"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_representation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_material"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_includeLayers"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_excludeLayers"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_layerOverridePriority"));
            serializedObject.ApplyModifiedProperties();

            var composite = (Composite)target;
            if (composite.IsGeometryGenerationRunning)
            {
                EditorGUILayout.HelpBox("Generating geometry in the background. The previous Collider remains active until completion.", MessageType.Info);
                Repaint();
            }

            if (GUILayout.Button("Use Child Sources"))
            {
                var children = composite.GetComponentsInChildren<Source>(true);

                Undo.RecordObject(composite, "Use Child Sources");
                serializedObject.Update();
                SerializedProperty entries = serializedObject.FindProperty("_sources");
                entries.arraySize = children.Length;
                for (int i = 0; i < children.Length; i++)
                {
                    SerializedProperty entry = entries.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("source").objectReferenceValue = children[i];
                    entry.FindPropertyRelative("collider").objectReferenceValue = null;
                }
                serializedObject.ApplyModifiedProperties();
            }

            if (GUILayout.Button("Generate Geometry"))
            {
                composite.RequestGeometryGeneration();
                EditorUtility.SetDirty(composite);
                if (composite.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(composite.gameObject.scene);
                }
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying || composite.IsGeometryGenerationRunning || composite.GetGeneratedMeshes().Length == 0))
            {
                if (GUILayout.Button("Save Generated Meshes..."))
                {
                    SaveGeneratedMeshes(composite);
                }
            }
        }

        private static void SaveGeneratedMeshes(Composite composite)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save CompositeCollider3D Meshes", $"{composite.name} Colliders", "asset",
                "Choose an Assets folder for the generated meshes.");
            if (string.IsNullOrEmpty(path)) return;
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            Mesh[] originals = composite.GetGeneratedMeshes();
            var saved = new Mesh[originals.Length];
            try
            {
                for (int i = 0; i < originals.Length; i++)
                {
                    saved[i] = Instantiate(originals[i]);
                    saved[i].name =
                        i == 0
                        ? "Composite3D Result"
                        : "Convex " + (i - 1);
                    if (i == 0)
                    {
                        AssetDatabase.CreateAsset(saved[i], path);
                    }
                    else
                    {
                        AssetDatabase.AddObjectToAsset(saved[i], saved[0]);
                    }
                }
                AssetDatabase.SaveAssets();
                composite.UseSavedMeshes(saved);
                EditorUtility.SetDirty(composite);
                if (composite.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(composite.gameObject.scene);
                }
                Debug.Log($"CompositeCollider3D meshes saved to {path}", composite);
            }
            catch (Exception e)
            {
                if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                else
                {
                    foreach (Mesh mesh in saved)
                    {
                        if (mesh != null)
                        {
                            DestroyImmediate(mesh);
                        }
                    }
                }
                Debug.LogError($"Could not save CompositeCollider3D meshes: {e}", composite);
            }
        }
    }

    [CustomPropertyDrawer(typeof(Composite.SourceEntry))]
    public sealed class CompositeColliderSourceEntryDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight * 2f + EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            float line = EditorGUIUtility.singleLineHeight;
            Rect first = new Rect(
                position.x,
                position.y,
                position.width,
                line);
            Rect second = new Rect(
                    position.x,
                    position.y + line + EditorGUIUtility.standardVerticalSpacing,
                    position.width,
                    line);
            SerializedProperty source = property.FindPropertyRelative("source");
            EditorGUI.PropertyField(first, source, label);
            if (source.objectReferenceValue == null)
            {
                EditorGUI.PropertyField(
                    second,
                    property.FindPropertyRelative("collider"),
                    new GUIContent("Collider (Merge)"));
            }
            else
            {
                EditorGUI.LabelField(second, "Collider and Operation: Source component");
            }
            EditorGUI.EndProperty();
        }
    }
}
