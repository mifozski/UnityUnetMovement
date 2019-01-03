using UnityEngine;
using System.Collections.Generic;
using Mirror;

// Server-authoritative movement with Client-side prediction and reconciliation
// Author:gennadiy.shvetsov@gmail.com
[RequireComponent(typeof(CharacterController))]
[NetworkSettings(sendInterval = 0.05f)]
public class NetworkMovement : NetworkBehaviour
{
    #region Declarations

    CharacterController characterController;

    // These are just for visual debugging via inspector
    [SerializeField]
    private bool _isServer;
    [SerializeField]
    private bool _isLocalPlayer;
    [SerializeField]
    private bool _isClient;
    [SerializeField]
    private bool _hasAuthority;

    [SerializeField]
    private bool _isGrounded;


    public float verticalMouseLookLimit = 170f;
    private float _verticalSpeed = 0f;

    public float _snapDistance = 1f;

    // this is a local client setting that will be sent with inputs and relayed with results
    public bool mouseSteer = true;

    public float groundSpeed = 30f;
    public float rotateSpeed = 200f;
    public float mouseSensitivity = 100f;

    [SerializeField]
    private bool _jump = false;

    public float _jumpHeight = 10f;

    // This struct would be used to collect player inputs
    [System.Serializable]
    public struct Inputs
    {
        public float forward;
        public float sides;
        public float vertical;
        public float pitch;
        public float yaw;
        public bool mouse;
        public bool sprint;
        public bool crouch;

        public float timeStamp;
    }

    [System.Serializable]
    public struct SyncInputs
    {
        public sbyte forward;
        public sbyte sides;
        public sbyte vertical;
        public float pitch;
        public float yaw;
        public bool mouse;
        public bool sprint;
        public bool crouch;

        public float timeStamp;
    }

    // This struct would be used to collect results of Move and Rotate functions
    [System.Serializable]
    public struct Results
    {
        public Quaternion rotation;
        public Vector3 position;
        public bool sprinting;
        public bool crouching;
        public bool mousing;
        public float timeStamp;
    }

    [System.Serializable]
    public struct SyncResults
    {
        public Vector3 position;
        public ushort pitch;
        public ushort yaw;
        public bool mousing;
        public bool sprinting;
        public bool crouching;
        public float timeStamp;
    }

    // Synced from server to all clients
    [SyncVar(hook = "RecieveResults")]
    private SyncResults syncResults = new SyncResults();

    [SerializeField]
    private Inputs _inputs;

    [SerializeField]
    private Results rcvdResults;

    [SerializeField]
    private Results _results;

    // Owner client and server would store it's inputs in this list
    [SerializeField]
    private List<Inputs> _inputsList = new List<Inputs>();

    // This list stores results of movement and rotation.
    // Needed for non-owner client interpolation
    [SerializeField]
    private List<Results> _resultsList = new List<Results>();

    // Interpolation related variables
    private bool _playData = false;
    private float _dataStep = 0f;
    private float _lastTimeStamp = 0f;
    // private bool _jumping = false;
    private Vector3 _startPosition;
    private Quaternion _startRotation;

    private float _step = 0;

    #endregion

    #region Monobehaviors

    private void Awake()
    {
        Debug.LogFormat("Awake {0}", transform.position);
    }

    void Start()
    {
        Debug.LogFormat("Start {0}", transform.position);
        // Set start position to instantiate position instead of 0,0,0
        _results.position = transform.position;
        _results.rotation = transform.rotation;
        characterController = GetComponent<CharacterController>();
    }

    void Update()
    {
        // These are just for visual debugging via inspector
        _isServer = isServer;
        _isLocalPlayer = isLocalPlayer;
        _isClient = isClient;
        _hasAuthority = hasAuthority;

        if (isLocalPlayer)
        {
            if (Input.GetButtonUp("SteerMode"))
                mouseSteer = !mouseSteer;

            // Getting clients inputs
            GetInputs(ref _inputs);
        }
    }

    private bool Grounded()
    {
        if (characterController == null) return true;

        Vector3 origin = transform.position + new Vector3(0, -characterController.height * 0.5f, 0);
        Vector3 direction = Vector3.down;
        float maxDistance = 0.1f;
        return Physics.Raycast(origin, direction, maxDistance);
    }

    void FixedUpdate()
    {
        _isGrounded = Grounded();

        if (isLocalPlayer)
        {
            _inputs.timeStamp = Time.time;
            // Client side prediction for non-authoritative client or plane movement and rotation for listen server/host
            Vector3 lastPosition = _results.position;
            Quaternion lastRotation = _results.rotation;
            bool lastCrouch = _results.crouching;
            _results.rotation = Rotate(_inputs, _results);
            _results.mousing = Mouse(_inputs, _results);
            _results.sprinting = Sprint(_inputs, _results);
            _results.crouching = Crouch(_inputs, _results);
            _results.position = Move(_inputs, _results);
            if (hasAuthority)
            {
                // Listen server/host part
                // Sending results to other clients(state sync)
                if (_dataStep >= GetNetworkSendInterval())
                {
                    if (Vector3.Distance(_results.position, lastPosition) > 0f || Quaternion.Angle(_results.rotation, lastRotation) > 0f || _results.crouching != lastCrouch)
                    {
                        _results.timeStamp = _inputs.timeStamp;
                        // Struct need to be fully new to count as dirty 
                        // Convering some of the values to get less traffic
                        SyncResults tempResults;
                        tempResults.position = _results.position;
                        tempResults.pitch = (ushort)(_results.rotation.eulerAngles.y * 182f);
                        tempResults.yaw = (ushort)(_results.rotation.eulerAngles.x * 182f);
                        tempResults.mousing = _results.mousing;
                        tempResults.sprinting = _results.sprinting;
                        tempResults.crouching = _results.crouching;
                        tempResults.timeStamp = _results.timeStamp;
                        syncResults = tempResults;
                    }
                    _dataStep = 0f;
                }
                _dataStep += Time.fixedDeltaTime;
            }
            else
            {
                // Owner client. Non-authoritative part
                // Add inputs to the inputs list so they could be used during reconciliation process
                if (Vector3.Distance(_results.position, lastPosition) > 0f || Quaternion.Angle(_results.rotation, lastRotation) > 0f || _results.crouching != lastCrouch)
                    _inputsList.Add(_inputs);

                // Sending inputs to the server
                // Unfortunately there is no method overload for [Command] so I need to write several almost similar functions
                // This one is needed to save on network traffic
                SyncInputs syncInputs;
                syncInputs.forward = (sbyte)(_inputs.forward * 127f);
                syncInputs.sides = (sbyte)(_inputs.sides * 127f);
                syncInputs.vertical = (sbyte)(_inputs.vertical * 127f);
                if (Vector3.Distance(_results.position, lastPosition) > 0f)
                {
                    if (Quaternion.Angle(_results.rotation, lastRotation) > 0f)
                        Cmd_MovementRotationInputs(syncInputs.forward, syncInputs.sides, syncInputs.vertical, _inputs.pitch, _inputs.yaw, _inputs.mouse, _inputs.sprint, _inputs.crouch, _inputs.timeStamp);
                    else
                        Cmd_MovementInputs(syncInputs.forward, syncInputs.sides, syncInputs.vertical, _inputs.sprint, _inputs.crouch, _inputs.timeStamp);
                }
                else
                {
                    if (Quaternion.Angle(_results.rotation, lastRotation) > 0f)
                        Cmd_RotationInputs(_inputs.pitch, _inputs.yaw, _inputs.mouse, _inputs.crouch, _inputs.timeStamp);
                    else if (_results.crouching != lastCrouch)
                        Cmd_OnlyStances(_inputs.crouch, _inputs.timeStamp);
                }
            }
        }
        else
        {
            if (hasAuthority)
            {
                // Server

                // Check if there is atleast one record in inputs list
                if (_inputsList.Count == 0)
                    return;

                // Move and rotate part. Nothing interesting here
                Inputs inputs = _inputsList[0];
                _inputsList.RemoveAt(0);
                Vector3 lastPosition = _results.position;
                Quaternion lastRotation = _results.rotation;
                bool lastCrouch = _results.crouching;
                _results.rotation = Rotate(inputs, _results);
                _results.mousing = Mouse(inputs, _results);
                _results.sprinting = Sprint(inputs, _results);
                _results.crouching = Crouch(inputs, _results);
                _results.position = Move(inputs, _results);

                // Sending results to other clients(state sync)
                if (_dataStep >= GetNetworkSendInterval())
                {
                    if (Vector3.Distance(_results.position, lastPosition) > 0f || Quaternion.Angle(_results.rotation, lastRotation) > 0f || _results.crouching != lastCrouch)
                    {
                        // Struct need to be fully new to count as dirty 
                        // Convering some of the values to get less traffic
                        _results.timeStamp = inputs.timeStamp;
                        SyncResults tempResults;
                        tempResults.position = _results.position;
                        tempResults.pitch = (ushort)(_results.rotation.eulerAngles.x * 182f);
                        tempResults.yaw = (ushort)(_results.rotation.eulerAngles.y * 182f);
                        tempResults.mousing = _results.mousing;
                        tempResults.sprinting = _results.sprinting;
                        tempResults.crouching = _results.crouching;
                        tempResults.timeStamp = _results.timeStamp;
                        syncResults = tempResults;
                    }
                    _dataStep = 0;
                }
                _dataStep += Time.fixedDeltaTime;
            }
            else
            {
                // Non-owner client a.k.a. dummy client
                // there should be at least two records in the results list so it would be possible to interpolate between them in case if there would be some dropped packed or latency spike
                // And yes this stupid structure should be here because it should start playing data when there are at least two records and continue playing even if there is only one record left 
                if (_resultsList.Count == 0)
                    _playData = false;

                if (_resultsList.Count >= 2)
                    _playData = true;

                if (_playData)
                {
                    if (_dataStep == 0f)
                    {
                        _startPosition = _results.position;
                        _startRotation = _results.rotation;
                    }
                    _step = 1f / (GetNetworkSendInterval());
                    _results.position = Vector3.Lerp(_startPosition, _resultsList[0].position, _dataStep);
                    _results.rotation = Quaternion.Slerp(_startRotation, _resultsList[0].rotation, _dataStep);
                    _results.mousing = _resultsList[0].mousing;
                    _results.sprinting = _resultsList[0].sprinting;
                    _results.crouching = _resultsList[0].crouching;
                    _dataStep += _step * Time.fixedDeltaTime;
                    if (_dataStep >= 1f)
                    {
                        _dataStep = 0;
                        _resultsList.RemoveAt(0);
                    }
                }

                UpdatePosition(_results.position);
                UpdateRotation(_results.rotation);
                UpdateMouse(_results.mousing);
                UpdateSprinting(_results.sprinting);
                UpdateCrouch(_results.crouching);
            }
        }
    }

    #endregion

    #region Helpers

    sbyte RoundToLargest(float inp)
    {
        if (inp > 0f)
            return 1;
        else if (inp < 0f)
            return -1;

        return 0;
    }

    #endregion

    #region ClientCallback

    // Updating Clients with server states
    [ClientCallback]
    void RecieveResults(SyncResults syncResults)
    {
        if (!isClient) return;
        Debug.Log("RecieveResults");

        // Converting values back
        rcvdResults.rotation = Quaternion.Euler((float)syncResults.pitch / 182, (float)syncResults.yaw / 182, 0);

        rcvdResults.position = syncResults.position;
        rcvdResults.mousing = syncResults.mousing;
        rcvdResults.sprinting = syncResults.sprinting;
        rcvdResults.crouching = syncResults.crouching;
        rcvdResults.timeStamp = syncResults.timeStamp;

        // Discard out of order results
        if (rcvdResults.timeStamp <= _lastTimeStamp)
            return;

        _lastTimeStamp = rcvdResults.timeStamp;

        // Non-owner client
        if (!isLocalPlayer && !hasAuthority)
        {
            // Adding results to the results list so they can be used in interpolation process
            rcvdResults.timeStamp = Time.time;
            _resultsList.Add(rcvdResults);
        }

        // Owner client
        // Server client reconciliation process should be executed in order to sync client's
        // rotation and position with server values but do it without jittering
        if (isLocalPlayer && !hasAuthority)
        {
            // Update client's position and rotation with ones from server 
            _results.rotation = rcvdResults.rotation;
            _results.position = rcvdResults.position;
            int foundIndex = -1;

            // Search recieved time stamp in client's inputs list
            for (int index = 0; index < _inputsList.Count; index++)
            {
                // If time stamp found run through all inputs starting from needed time stamp 
                if (_inputsList[index].timeStamp > rcvdResults.timeStamp)
                {
                    foundIndex = index;
                    break;
                }
            }

            if (foundIndex == -1)
            {
                // Clear Inputs list if no needed records found 
                while (_inputsList.Count != 0)
                    _inputsList.RemoveAt(0);

                return;
            }

            // Replay recorded inputs
            for (int subIndex = foundIndex; subIndex < _inputsList.Count; subIndex++)
            {
                _results.rotation = Rotate(_inputsList[subIndex], _results);
                _results.crouching = Crouch(_inputsList[subIndex], _results);
                _results.sprinting = Sprint(_inputsList[subIndex], _results);

                _results.position = Move(_inputsList[subIndex], _results);
            }

            // Remove all inputs before time stamp
            int targetCount = _inputsList.Count - foundIndex;
            while (_inputsList.Count > targetCount)
                _inputsList.RemoveAt(0);
        }
    }

    #endregion

    #region ServerCommands

    // Standing on spot
    [Command]
    void Cmd_OnlyStances(bool crouch, float timeStamp)
    {
        Debug.Log("Cmd_OnlyStances");
        if (isServer && !isLocalPlayer)
        {
            Inputs inputs;
            inputs.forward = 0f;
            inputs.sides = 0f;
            inputs.vertical = 0f;
            inputs.pitch = 0f;
            inputs.yaw = 0;
            inputs.mouse = false;
            inputs.sprint = false;
            inputs.crouch = crouch;
            inputs.timeStamp = timeStamp;
            _inputsList.Add(inputs);
        }
    }

    // Only rotation inputs sent 
    [Command]
    void Cmd_RotationInputs(float pitch, float yaw, bool mouse, bool crouch, float timeStamp)
    {
        Debug.Log("Cmd_RotationInputs");
        if (isServer && !isLocalPlayer)
        {
            Inputs inputs;
            inputs.forward = 0f;
            inputs.sides = 0f;
            inputs.vertical = 0f;
            inputs.pitch = pitch;
            inputs.yaw = yaw;
            inputs.mouse = mouse;
            inputs.sprint = false;
            inputs.crouch = crouch;
            inputs.timeStamp = timeStamp;
            _inputsList.Add(inputs);
        }
    }

    // Rotation and movement inputs sent 
    [Command]
    void Cmd_MovementRotationInputs(sbyte forward, sbyte sides, sbyte vertical, float pitch, float yaw, bool mouse, bool sprint, bool crouch, float timeStamp)
    {
        Debug.Log("Cmd_MovementRotationInputs");
        if (isServer && !isLocalPlayer)
        {
            Inputs inputs;
            inputs.forward = Mathf.Clamp((float)forward / 127f, -1f, 1f);
            inputs.sides = Mathf.Clamp((float)sides / 127f, -1f, 1f);
            inputs.vertical = Mathf.Clamp((float)vertical / 127f, -1f, 1f);
            inputs.pitch = pitch;
            inputs.yaw = yaw;
            inputs.mouse = mouse;
            inputs.sprint = sprint;
            inputs.crouch = crouch;
            inputs.timeStamp = timeStamp;
            _inputsList.Add(inputs);
        }
    }

    // Only movements inputs sent
    [Command]
    void Cmd_MovementInputs(sbyte forward, sbyte sides, sbyte vertical, bool sprint, bool crouch, float timeStamp)
    {
        Debug.Log("Cmd_MovementInputs");
        if (isServer && !isLocalPlayer)
        {
            Inputs inputs;
            inputs.forward = Mathf.Clamp((float)forward / 127f, -1f, 1f);
            inputs.sides = Mathf.Clamp((float)sides / 127f, -1f, 1f);
            inputs.vertical = Mathf.Clamp((float)vertical / 127f, -1f, 1f);
            inputs.pitch = 0f;
            inputs.yaw = 0f;
            inputs.mouse = false;
            inputs.sprint = sprint;
            inputs.crouch = crouch;
            inputs.timeStamp = timeStamp;
            _inputsList.Add(inputs);
        }
    }

    #endregion

    #region Virtuals

    // Next virtual functions can be changed in inherited class for custom movement and rotation mechanics
    // So it would be possible to control for example humanoid or vehicle from one script just by changing controlled pawn

    public virtual void GetInputs(ref Inputs inputs)
    {
        // Don't use one frame events in this part
        // It would be processed incorrectly 
        inputs.sides = RoundToLargest(Input.GetAxis("Horizontal"));
        inputs.forward = RoundToLargest(Input.GetAxis("Vertical"));
        inputs.sprint = Input.GetButton("Sprint");
        inputs.crouch = Input.GetButton("Crouch");
        inputs.mouse = mouseSteer;

        if (mouseSteer)
        {
            inputs.pitch = Input.GetAxis("Mouse X") * mouseSensitivity * Time.fixedDeltaTime / Time.deltaTime;
            inputs.yaw = -Input.GetAxis("Mouse Y") * mouseSensitivity * Time.fixedDeltaTime / Time.deltaTime;
        }
        else
        {
            inputs.pitch = Input.GetAxis("Pitch") * rotateSpeed * Time.fixedDeltaTime / Time.deltaTime;
            inputs.yaw = -Input.GetAxis("Yaw") * rotateSpeed * Time.fixedDeltaTime / Time.deltaTime;
        }

        float verticalTarget = -1;
        if (_isGrounded)
        {
            if (Input.GetButton("Jump"))
                _jump = true;

            verticalTarget = 0;
            inputs.vertical = 0;
        }

        if (_jump)
        {
            verticalTarget = 1;

            if (inputs.vertical >= 0.9f)
                _jump = false;
        }

        inputs.vertical = Mathf.Lerp(inputs.vertical, verticalTarget, 10f * Time.deltaTime);
    }

    public virtual void UpdatePosition(Vector3 newPosition)
    {
        if (Vector3.Distance(newPosition, transform.position) > _snapDistance)
            transform.position = newPosition;
        else
            if (characterController != null) characterController.Move(newPosition - transform.position);
    }

    public virtual void UpdateRotation(Quaternion newRotation)
    {
        transform.rotation = Quaternion.Euler(0, newRotation.eulerAngles.y, 0);
    }

    public virtual void UpdateMouse(bool sprinting) { }

    public virtual void UpdateSprinting(bool sprinting) { }

    public virtual void UpdateCrouch(bool crouch) { }

    public virtual Vector3 Move(Inputs inputs, Results current)
    {
        transform.position = current.position;
        float speed = groundSpeed;
        if (current.crouching)
            speed = groundSpeed * .5f;

        if (current.sprinting)
            speed = groundSpeed * 1.6f;

        if (inputs.vertical > 0)
            _verticalSpeed = inputs.vertical * _jumpHeight;
        else
            _verticalSpeed = inputs.vertical * Physics.gravity.magnitude;

        if (characterController != null) characterController.Move(transform.TransformDirection((Vector3.ClampMagnitude(new Vector3(inputs.sides, 0, inputs.forward), 1) * speed) + new Vector3(0, _verticalSpeed, 0)) * Time.fixedDeltaTime);
        return transform.position;
    }

    public virtual bool Mouse(Inputs inputs, Results current)
    {
        return inputs.mouse;
    }

    public virtual bool Sprint(Inputs inputs, Results current)
    {
        return inputs.sprint;
    }

    public virtual bool Crouch(Inputs inputs, Results current)
    {
        return inputs.crouch;
    }

    public virtual Quaternion Rotate(Inputs inputs, Results current)
    {
        transform.rotation = current.rotation;

        float mHor = transform.eulerAngles.y - inputs.yaw * Time.fixedDeltaTime;
        float mVert = transform.eulerAngles.x - inputs.pitch * Time.fixedDeltaTime;

        if (mVert > 180f)
            mVert -= 360f;

        mVert = Mathf.Clamp(mVert, -verticalMouseLookLimit * 0.5f, verticalMouseLookLimit * 0.5f);

        transform.rotation = Quaternion.Euler(mVert, mHor, 0f);

        return transform.rotation;
    }

    #endregion
}
