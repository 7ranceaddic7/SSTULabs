﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SSTUTools
{
    public class SolarModule<T> : AnimationModule<T> where T : PartModule
    {

        /// <summary>
        /// GUI status field for the solar panel.  Updated every Update()?
        /// </summary>
        private BaseField panelStatusField;

        /// <summary>
        /// Persistent data field for panel rotations and broken status.  Backing field should be a string, so arbitrary data can be stored.
        /// </summary>
        private BaseField rotationPersistenceField;

        /// <summary>
        /// Live solar panel data, including references to pivot and suncatcher transforms.  Includes tracking of 'broken' status for each panel.<para/>
        /// Each entry in the array is a single 'panel' denoted by a shared breakage point.
        /// </summary>
        private PanelData[] panelData;

        /// <summary>
        /// Internal flag tracking if the solar panel should be doing the 'pre-retract' rotation back towards default orientation.  Not tracked persistently -- if part is saved out while close-lerp is in action, it will actually save out as if it were deployed
        /// </summary>
        private bool closingLerp;

        private string panelStatus
        {
            get { return panelStatusField.GetValue<string>(panelStatusField); }
            set { panelStatusField.SetValue(value, panelStatusField); }
        }

        public SolarModule(Part part, T module, BaseField animationPersistence, BaseField rotationPersistence, BaseField panelStatusField, BaseEvent deploy, BaseEvent retract) : base(part, module, animationPersistence, null, deploy, retract)
        {
            this.rotationPersistenceField = rotationPersistence;
            this.panelStatusField = panelStatusField;
        }

        public override void onRetractEvent()
        {
            if (animState == AnimState.STOPPED_END)
            {
                closingLerp = true;
            }
            else
            {
                base.onRetractEvent();
            }            
        }

        /// <summary>
        /// Must be called after the animations have been setup, so that they may be used
        /// to sample and properly setup default rotations for the pivot transforms
        /// </summary>
        /// <param name="node"></param>
        public void setupSolarPanelData(ConfigNode node, Transform root)
        {
            ConfigNode[] panelNodes = node.GetNodes("PANEL");
            int len = panelNodes.Length;
            panelData = new PanelData[len];
            for (int i = 0; i < len; i++)
            {
                panelData[i] = new PanelData(panelNodes[i], root);
            }
            initializeRotations();
        }

        /// <summary>
        /// Should be called from Update() to update the solar panel transform rotations.
        /// This just rotates the panel pivots towards the solar target, does not check occlusion or ec-output
        /// but will be disabled if the panel is already occluded.
        /// </summary>
        public void solarUpdate()
        {
            //TODO -- support solar panels that lack animations (static panels)
            //TODO -- support solar panel animation locking -- this should have separate lock and angle sliders for main and secondary transforms
            //TODO -- how useful is the locking feature, really?
            //only update if animation is set to deployed
            if (animState != AnimState.STOPPED_END)
            {
                return;
            }
            int len = panelData.Length;
            if (closingLerp)//active in both editor and flight
            {
                bool finished = true;
                for (int i = 0; i < len; i++)
                {
                    finished = finished && panelData[i].panelUpdateRetract();
                }
                if (finished)
                {
                    closingLerp = false;
                    setAnimState(AnimState.PLAYING_BACKWARD);
                }
            }
            else if(HighLogic.LoadedSceneIsFlight)//sun tracking only active in flight
            {
                Vector3 sunPos = FlightGlobals.Bodies[0].transform.position;
                for (int i = 0; i < len; i++)
                {
                    panelData[i].panelUpdate(sunPos);
                }
            }
            //noop in editor if not lerping closed
        }

        /// <summary>
        /// Should be called on fixed-update to calculate the EC output from solar panels.  Includes raycasts
        /// for occlusion checks, as well as
        /// </summary>
        public void solarFixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || part.vessel == null)
            {
                return;
            }
            float distMult = (float)(part.vessel.solarFlux / PhysicsGlobals.SolarLuminosityAtHome);
            if (distMult == 0)//occluded, zero solar flux input on vessel
            {
                //use current main body as the occluder, even though it might be something else in the case of lunar eclipse/etc
                if (FlightGlobals.currentMainBody != null)
                {
                    panelStatus = "Occluded: " + FlightGlobals.currentMainBody.name;
                }
                else
                {
                    panelStatus = "Occluded: Unknown";
                }
                //TODO loop through panels and set them to 'occluded' status so that they do not update
                return;
            }
            Vector3 solarTarget = FlightGlobals.Bodies[0].transform.position;
            string occluder = string.Empty;
            float panelOutput = 0f;
            float totalOutput = 0f;
            int len = panelData.Length;
            for (int i = 0; i < len; i++)
            {
                panelOutput = panelData[i].panelFixedUpdate(solarTarget);
                if (panelOutput == 0)
                {
                    occluder = panelData[i].occluderName;
                }
            }

            float temperatureMultiplier = 1.0f;//TODO -- add temp curve/multiplier
            totalOutput *= temperatureMultiplier * distMult;//per-second output value
            //use current this to update gui status before converting to delta time updates
            if (totalOutput > 0)
            {
                panelStatus = totalOutput + " EC/s";

                totalOutput *= TimeWarp.fixedDeltaTime;
                MonoBehaviour.print("TODO -- update part resources for EC generation");
                //TODO update part resources
            }
            else
            {
                panelStatus = "Occluded: " + occluder;
            }
        }

        //TODO
        /// <summary>
        /// Should be called from owning part-module when solar panels are to be repaired.
        /// </summary>
        public void onPanelRepairEvent()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Initialize the default pivot orientations and restore any rotations from persistent data
        /// </summary>
        private void initializeRotations()
        {
            float time = animTime;
            //sample to deployed state
            setAnimTime(1, true);
            string[] persistentDataSplits = persistentData.Split(';');
            string data;
            int len = panelData.Length;
            for (int i = 0; i < len; i++)
            {
                if (i < persistentDataSplits.Length)
                {
                    data = persistentDataSplits[i];
                }
                else
                {
                    data = string.Empty;
                }
                //load persistence and restore previous rotations, if applicable
                panelData[i].initializeRotations(data);
            }
            //return animation state to previous state
            setAnimTime(time, true);
        }

        /// <summary>
        /// Update the persistent data field with the live data from the current panel configuration.
        /// </summary>
        public void updateSolarPersistence()
        {
            // Data format = mainDefRot-mainCurRot:secDefRot-secCurRot:brokenStatus;(repeat after semicolon for next panel)
            string data = string.Empty;
            int len = panelData.Length;
            for (int i = 0; i < len; i++)
            {
                if (i > 0) { data = data + ";"; }
                data = data + panelData[i].getPersistentData();
            }
            persistentData = data;
        }

    }

    public enum Axis
    {
        XPlus,
        XNeg,
        YPlus,
        YNeg,
        ZPlus,
        ZNeg
    }

    /// <summary>
    /// Wrapper for a single 'solar panel' -- suncatchers, pivots, broken status
    /// </summary>
    public class PanelData
    {

        /// <summary>
        /// Dynamic-pressure needed to break the panel if deployed while moving through an atmosphere.
        /// </summary>
        public readonly float breakPressure;

        /// <summary>
        /// G-force required to break the panel if it is deployed while the force is experienced.
        /// The exact g-force experienced by the panel is the input G multiplied
        /// by the dot product of the G vector and the panels break transform axis.
        /// </summary>
        public readonly float breakGs;

        /// <summary>
        /// Persistent tracking for if this individual panel is broken or not
        /// </summary>
        public bool isBroken = false;

        /// <summary>
        /// Main pivot container
        /// </summary>
        public SolarPivotData mainPivot;

        /// <summary>
        /// Secondary pivot container
        /// </summary>
        public SolarPivotData secondPivot;

        /// <summary>
        /// Suncatcher containers
        /// </summary>
        public SuncatcherData[] suncatchers;

        /// <summary>
        /// Name of the occluder, if any.  Updated during panel power update processing.
        /// </summary>
        public string occluderName = string.Empty;

        public PanelData(ConfigNode node, Transform root)
        {
            string mainName = node.GetStringValue("mainPivot", string.Empty);
            if (!string.IsNullOrEmpty(mainName))
            {
                int mainIndex = node.GetIntValue("mainPivotIndex", 0);
                Axis mainSunAxis = node.getAxis("mainSunAxis", Axis.ZPlus);
                Axis mainRotAxis = node.getAxis("mainRotAxis", Axis.XPlus);
                float speed = node.GetFloatValue("mainPivotSpeed", 10f);
                Transform[] trs = root.FindChildren(mainName);
                mainPivot = new SolarPivotData(trs[mainIndex], speed, mainSunAxis, mainRotAxis);

                string secondName = node.GetStringValue("secondPivot", string.Empty);
                if (!string.IsNullOrEmpty(secondName))
                {
                    speed = node.GetFloatValue("secondPivotSpeed", 10f);
                    int secondIndex = node.GetIntValue("secondPivotIndex", 0);
                    Axis secSunAxis = node.getAxis("secondSunAxis", Axis.ZPlus);
                    Axis secRotAxis = node.getAxis("secondRotAxis", Axis.XPlus);
                    trs = root.FindChildren(secondName);
                    secondPivot = new SolarPivotData(trs[secondIndex], speed, secSunAxis, secRotAxis);
                }
            }
            ConfigNode[] suncatcherNodes = node.GetNodes("SUNCATCHER");
            int len = suncatcherNodes.Length;
            suncatchers = new SuncatcherData[len];
            for (int i = 0; i < len; i++)
            {
                suncatchers[i] = new SuncatcherData(suncatcherNodes[i], root);
            }
        }

        /// <summary>
        /// Updates the panels sun-tracking pivots in 'retracting' mode.<para/>
        /// Returns true when finished and ready for retract animation
        /// </summary>
        public bool panelUpdateRetract()
        {
            bool mainDone = true;
            bool secondDone = true;
            if (mainPivot != null)
            {
                mainDone = mainPivot.rotateTowardsDefault();
                if (secondPivot != null)
                {
                    secondDone = secondPivot.rotateTowardsDefault();
                }
            }
            return mainDone && secondDone;
        }

        /// <summary>
        /// Updates the panels sun-tracking pivots, using the config-value for tracking speed.
        /// </summary>
        /// <param name="solarSource"></param>
        public void panelUpdate(Vector3 solarSource)
        {
            if (isBroken) { return; }
            if (!string.IsNullOrEmpty(occluderName)) { return; }
            if (mainPivot != null)
            {
                mainPivot.rotateTowards(solarSource);
                if (secondPivot != null)
                {
                    secondPivot.rotateTowards(solarSource);
                }
            }
        }

        /// <summary>
        /// Updates the panels suncatchers -- checking for occlusion and calcuating EC output if not occluded
        /// </summary>
        /// <param name="solarSource"></param>
        /// <returns></returns>
        public float panelFixedUpdate(Vector3 solarSource)
        {
            occluderName = string.Empty;
            if (isBroken) { return 0; }
            int len = suncatchers.Length;
            float panelOutput = 0f;
            float totalOutput = 0f;
            for (int i = 0; i < len; i++)
            {
                panelOutput = suncatchers[i].calcRawOutput(solarSource);
                if (panelOutput <= 0)//was occluded
                {
                    occluderName = suncatchers[i].occluderName;
                }
                totalOutput += panelOutput;
            }
            return totalOutput;
        }

        /// <summary>
        /// Initialize the rotations of the pivots in this panel.<para/>
        /// The input persistent data string should be of the format: mainPivotData:secondPivotData:brokenStatus.<para/>
        /// E.g.  x,y,z,w-x,y,z,w:x,y,z,w-x,y,z,w:false
        /// </summary>
        /// <param name="persistentData"></param>
        public void initializeRotations(string persistentData)
        {
            string[] splitData = string.IsNullOrEmpty(persistentData) ? new string[] { "","","false"} : persistentData.Split(':');
            string mainPivotPersistence = splitData[0];
            string secondPivotPersistence = splitData[1];
            string brokenPersistence = splitData[2];
            if (mainPivot != null)
            {
                mainPivot.initializeRotation(mainPivotPersistence);
            }
            if (secondPivot != null)
            {
                secondPivot.initializeRotation(secondPivotPersistence);
            }
            isBroken = brokenPersistence == "true";
        }

        /// <summary>
        /// Return the updated string representation of the persistent data for the current panel configuration for this SolarModule.<para/>
        /// E.g. x,y,z,w-x,y,z,w:x,y,z,w-x,y,z,w:false
        /// </summary>
        /// <returns></returns>
        public string getPersistentData()
        {
            string main = mainPivot == null ? "" : mainPivot.getPersistentData();
            string second = secondPivot==null ? "" : secondPivot.getPersistentData();
            string broken = isBroken ? "true" : "false";
            return main + ":" + second + ":" + broken;
        }

    }

    /// <summary>
    /// Container class for a single solar-panel 'suncatcher' transform.<para/>
    /// Includes methods to check for part-based occlusion through raycasting, and to calculate the raw (angle-only) energy output based on an input 'sun' position.
    /// </summary>
    public class SuncatcherData
    {

        /// <summary>
        /// The suncatcher transform
        /// </summary>
        public readonly Transform suncatcher;

        /// <summary>
        /// This axis of the suncatcher transform will be used to check for sun-angle
        /// </summary>
        public readonly Axis suncatcherAxis;

        /// <summary>
        /// Resource generation rate, in units per second.
        /// </summary>
        public readonly float resourceRate;

        /// <summary>
        /// Cached value of the last known 'occluder' -- updated whenever a suncatcher is occluded by a part.<para/>
        /// Examined in the outer solar-panel updating loop if the suncatcher returns 0 output energy.
        /// </summary>
        public string occluderName = string.Empty;

        /// <summary>
        /// Internal cache of hit-data... really don't think Unity raycast re-uses these references properly though, so probably not really useful.
        /// </summary>
        private RaycastHit hitData;

        public SuncatcherData(ConfigNode node, Transform root)
        {
            string sunName = node.GetStringValue("suncatcher");
            suncatcherAxis = node.getAxis("suncatcherAxis", Axis.ZPlus);
            int index = node.GetIntValue("suncatcherIndex", 0);
            Transform[] trs = root.FindChildren(sunName);
            suncatcher = trs[index];
            resourceRate = node.GetFloatValue("rate");
        }

        /// <summary>
        /// Return the raw output based on transform orientation.  Is not adjusted for distance or temperature.  Checks part occlusion, does not check for body occlusion.
        /// </summary>
        /// <param name="target"></param>
        public float calcRawOutput(Vector3 targetPos)
        {
            Vector3 panelFacing = suncatcher.getTransformAxis(suncatcherAxis);
            Vector3 directionToSun = (targetPos - suncatcher.position).normalized;
            float sunDot = Mathf.Clamp(Vector3.Dot(panelFacing, directionToSun), 0f, 1f);
            if (sunDot > 0 && checkPartOcclusion(directionToSun))
            {
                //occluded by part, zero out the resource output, this causes outer-loop power calc to examine the occluder name
                sunDot = 0;
            }
            return sunDot * resourceRate;
        }

        /// <summary>
        /// Do a short raycast on the suncatcher to determine if it is occluded.
        /// TODO -- only do raycast occlusion check every X ticks, and/or only raycast-occlusion-check from a single suncatcher per-panel (rather than every suncatcher)
        /// </summary>
        /// <param name="directionToSun"></param>
        /// <returns></returns>
        public bool checkPartOcclusion(Vector3 directionToSun)
        {
            if (Physics.Raycast(suncatcher.position, directionToSun, out hitData, 300f))
            {
                occluderName = hitData.transform.gameObject.name;
                return true;
            }
            return false;
        }

    }

    /// <summary>
    /// Container class for a single sun-tracking pivot.<para/>
    /// Contains methods to update rotations to point towards a target, a specific angle, or rotate back towards default orientation.
    /// </summary>
    public class SolarPivotData
    {

        /// <summary>
        /// The pivot transform.
        /// </summary>
        public readonly Transform pivot;

        /// <summary>
        /// Pivot transform will point this axis towards the sun
        /// </summary>
        public readonly Axis pivotSunAxis;

        /// <summary>
        /// Pivot transform will rotate around this axis
        /// </summary>
        public readonly Axis pivotRotationAxis;

        /// <summary>
        /// Pivot transform will rotate at this speed, in degrees-per-second
        /// </summary>
        public readonly float trackingSpeed = 10f;

        /// <summary>
        /// Pre-calculated rotation offset used during constraint updates.
        /// </summary>
        private readonly float rotationOffset = 0f;

        /// <summary>
        /// Cached default orientation of the pivot.  Used to properly rotate back to default rotation prior to solar panel retract animation.
        /// </summary>
        private Quaternion defaultOrientation;

        public SolarPivotData(Transform pivot, float trackingSpeed, Axis pivotSunAxis, Axis pivotRotationAxis)
        {
            this.pivot = pivot;
            this.trackingSpeed = trackingSpeed;
            this.pivotSunAxis = pivotSunAxis;
            this.pivotRotationAxis = pivotRotationAxis;

            //pre-calculate a rotation offset that is used during updating of the pivot rotation
            //this offset is used to speed up constraint calculation
            if (pivotRotationAxis==Axis.XPlus || pivotRotationAxis==Axis.XNeg)
            {
                if (pivotSunAxis == Axis.YPlus)
                {
                    rotationOffset = 90;
                }
                else if (pivotSunAxis == Axis.YNeg)
                {
                    rotationOffset = -90;
                }
                else if (pivotSunAxis == Axis.ZNeg)
                {
                    rotationOffset = 180f;
                }
                //implicit else ZPlus = 0f
            }
            else if (pivotRotationAxis == Axis.YPlus || pivotRotationAxis == Axis.YNeg)
            {
                if (pivotSunAxis == Axis.XPlus)
                {
                    rotationOffset = -90f;
                }
                else if (pivotSunAxis == Axis.XNeg)
                {
                    rotationOffset = 90f;
                }
                else if (pivotSunAxis == Axis.ZNeg)
                {
                    rotationOffset = 180f;
                }
                //implicit else ZPlus = 0f
            }
            else if (pivotRotationAxis == Axis.ZPlus || pivotRotationAxis == Axis.ZNeg)
            {
                if (pivotSunAxis == Axis.XPlus)
                {
                    rotationOffset = 90f;
                }
                else if (pivotSunAxis == Axis.XNeg)
                {
                    rotationOffset = -90f;
                }
                else if (pivotSunAxis == Axis.YNeg)
                {
                    rotationOffset = 180f;
                }
                //implicit else YPlus = 0f
            }
        }

        /// <summary>
        /// Sets the pivot transform to a specific local rotation
        /// </summary>
        /// <param name="localOrientation"></param>
        public void setRotation(Quaternion localOrientation)
        {
            pivot.localRotation = localOrientation;
        }

        /// <summary>
        /// Rotate the pivot towards the input target position.
        /// </summary>
        /// <param name="targetPos"></param>
        /// <returns></returns>
        public bool rotateTowards(Vector3 targetPos)
        {
            //this is the total angle needed to rotate (could be + or -)
            float rawAngle = getRotationAmount(targetPos);
            float absAngle = Mathf.Abs(rawAngle);
            float frameSpeed = trackingSpeed * Time.deltaTime;
            float frameAngle = 0f;

            bool finished = true;
            if (absAngle > frameSpeed)//too much for a single frame
            {
                finished = false;
                frameAngle = frameSpeed * Mathf.Sign(rawAngle);
            }
            else//implicit if (absAngle <= frameSpeed)//can be completed this frame
            {
                finished = true;
                frameAngle = rawAngle;
            }
            pivot.Rotate(pivot.getLocalAxis(pivotRotationAxis), frameAngle, Space.Self);
            MonoBehaviour.print("Updating rotation, finished: " + finished);
            return finished;
        }

        /// <summary>
        /// Should be used when a user-specified rotation position is desired.  The input angle is in 'degrees relative to default orientation', and can be positive or negative.
        /// Rotation direction will depend on the rotation axis specified, and whether the axis itself is positive or negative (XPlus vs XNeg)
        /// </summary>
        /// <param name="angle"></param>
        /// <returns></returns>
        public bool rotateTowardsAngle(float angle)
        {
            Vector3 localAxis = pivot.getLocalAxis(pivotRotationAxis);            
            Vector3 currentEuler = Vector3.Scale(pivot.localRotation.eulerAngles, localAxis);//this is the current local rotation, expressed in euler-angle-degrees (I hope it is degrees...)
            Vector3 destEuler = localAxis * angle;//this is the desired destination rotation, in euler-angle-degrees
            Vector3 rotation = currentEuler - destEuler;//this is the difference between the current and desired, in euler-angle-degrees
            float sqMag = rotation.sqrMagnitude;//sq-magnitude used to check for clamping
            float frameSpeed = trackingSpeed * Time.deltaTime;//maximum that can rotate this frame
            rotation.x = Mathf.Clamp(rotation.x, -frameSpeed, frameSpeed);
            rotation.y = Mathf.Clamp(rotation.y, -frameSpeed, frameSpeed);
            rotation.z = Mathf.Clamp(rotation.z, -frameSpeed, frameSpeed);
            pivot.Rotate(rotation, Space.Self);
            return sqMag == rotation.sqrMagnitude;//was unchanged during clamp, so rotation was below the frame speed, and thus is finished
        }

        /// <summary>
        /// Rotate the panel pivot towards its default orientation, using the currently configured rotation speed
        /// </summary>
        /// <returns></returns>
        public bool rotateTowardsDefault()
        {
            //cache current orientation
            Quaternion currentOrientation = pivot.localRotation;
            
            //update to the default orientation
            pivot.localRotation = defaultOrientation;
            
            //get a position vector that represents the world-space target from the default orientation
            Vector3 defaultRotTargetPos = pivot.transform.position + pivot.getTransformAxis(pivotSunAxis);

            //restore the actual current rotation
            pivot.localRotation = currentOrientation;

            //finally, update the actual rotation
            return rotateTowards(defaultRotTargetPos);
        }

        /// <summary>
        /// Return the number of degrees the panel needs to rotate around its rotation axis in order to point the
        /// sun-axis as close to the target-pos as is possible given the constrained rotation
        /// </summary>
        /// <param name="targetPos"></param>
        /// <returns></returns>
        public float getRotationAmount(Vector3 targetPos)
        {
            //local-space position of the target
            Vector3 localDiff = pivot.InverseTransformPoint(targetPos);
            float rotation = 0f;
            if (pivotRotationAxis == Axis.XPlus || pivotRotationAxis == Axis.XNeg)
            {
                //use y and z
                rotation = -Mathf.Atan2(localDiff.y, localDiff.z) * Mathf.Rad2Deg + rotationOffset;
            }
            else if (pivotRotationAxis == Axis.YPlus || pivotRotationAxis == Axis.YNeg)
            {
                //use x and z
                rotation = Mathf.Atan2(localDiff.x, localDiff.z) * Mathf.Rad2Deg + rotationOffset;
            }
            else if (pivotRotationAxis == Axis.ZPlus || pivotRotationAxis == Axis.ZNeg)
            {
                //use x and y
                rotation = -Mathf.Atan2(localDiff.x, localDiff.y) * Mathf.Rad2Deg + rotationOffset;
            }
            return rotation;
        }

        /// <summary>
        /// Input should be the persistent data for this single pivot.<para/>
        /// Should consist of two quaternions, using comma between members, and dash for separation between quats -- e.g. x,y,z,w-x,y,z,w<para/>
        /// If persitent data string is empty/null, default orientation is initialized to the current local rotation of the pivot.  This new default
        /// will be captured and saved to persistence the next time the part is saved.
        /// </summary>
        /// <param name="persistentData"></param>
        public void initializeRotation(string persistentData)
        {
            if (!string.IsNullOrEmpty(persistentData))
            {
                string[] splitData = persistentData.Split('-');
                float[] def = SSTUUtils.parseFloatArray(splitData[0]);
                float[] cur = SSTUUtils.parseFloatArray(splitData[1]);
                defaultOrientation = new Quaternion(def[0], def[1], def[2], def[3]);
                pivot.localRotation = new Quaternion(cur[0], cur[1], cur[2], cur[3]);
            }
            else
            {
                defaultOrientation = pivot.localRotation;
            }
        }

        /// <summary>
        /// Return the persistent data for this pivot.  Will be of the format x,y,z,w-x,y,z,w - the first xyzw set is 'default' local rotation, the second is 'current' local rotation.<para/>
        /// The default value is specifically saved out, as when KSP clones parts in the editor it skips past much of the standard part initialization routines, and does not serialize complex mod-added data classes properly.
        /// </summary>
        /// <returns></returns>
        public string getPersistentData()
        {
            Quaternion def = defaultOrientation;
            Quaternion cur = pivot.localRotation;
            return def.x+","+def.y+","+def.z+","+def.w+"-"+cur.x+","+cur.y+","+cur.z+","+cur.w;
        }

    }

}
