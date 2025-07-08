using UnityEngine;
using Ragnox.MeshOptimizer;

public class TestMeshOptimiser : MonoBehaviour
{
    public MeshFilter origionalMesh;

    public MeshFilter decimatedMesh;

    public float targetTriangleRatio = 0.05f;
    public float targetError = 0.5f;

    public void Start()
    {
        decimatedMesh.mesh = MeshOptimizerUnity.DecimateMesh(origionalMesh.mesh, targetTriangleRatio: targetTriangleRatio, targetError: targetError);
    }
}
