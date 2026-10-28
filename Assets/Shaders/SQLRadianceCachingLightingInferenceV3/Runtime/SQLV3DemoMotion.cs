using UnityEngine;
namespace Rackneh.InferenceLighting.SQLRadianceLiveV3
{
    public sealed class SQLV3DemoMotion : MonoBehaviour
    {
        public Transform movingObject, leftArm, rightArm;
        Vector3 origin;
        void Start() { if(movingObject) origin=movingObject.localPosition; }
        void Update()
        {
            float t=Time.time;
            if(movingObject) { movingObject.localPosition=origin+new Vector3(Mathf.Sin(t)*1.4f,0,0); movingObject.Rotate(0,40*Time.deltaTime,0); }
            if(leftArm) leftArm.localRotation=Quaternion.Euler(0,0,Mathf.Sin(t*2)*35);
            if(rightArm) rightArm.localRotation=Quaternion.Euler(0,0,-Mathf.Sin(t*2)*35);
        }
    }
}
