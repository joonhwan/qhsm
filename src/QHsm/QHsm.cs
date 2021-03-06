﻿// -----------------------------------------------------------------------------
//                            qf4net Library
//
// Port of Samek's Quantum Framework to C#. The implementation takes the liberty
// to depart from Miro Samek's code where the specifics of desktop systems 
// (compared to embedded systems) seem to warrant a different approach.
// Please see accompanying documentation for details.
// 
// Reference:
// Practical Statecharts in C/C++; Quantum Programming for Embedded Systems
// Author: Miro Samek, Ph.D.
// http://www.quantum-leaps.com/book.htm
//
// -----------------------------------------------------------------------------
//
// Copyright (C) 2003-2004, The qf4net Team
// All rights reserved
// Lead: Rainer Hessmer, Ph.D. (rainer@hessmer.org)
// 
//
//   Redistribution and use in source and binary forms, with or without
//   modification, are permitted provided that the following conditions
//   are met:
//
//     - Redistributions of source code must retain the above copyright
//        notice, this list of conditions and the following disclaimer. 
//
//     - Neither the name of the qf4net-Team, nor the names of its contributors
//        may be used to endorse or promote products derived from this
//        software without specific prior written permission. 
//
//   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
//   "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
//   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
//   FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
//   THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
//   INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//   (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
//   SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
//   HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
//   STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
//   ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
//   OF THE POSSIBILITY OF SUCH DAMAGE.
// -----------------------------------------------------------------------------
// Rev History:
// *Aug 26 2010: 
//  changed Dispatch() while loop to do-while, reflecting newer QHSM releases.
//  changed Init() to use raw TransitionTo() functionality...simplifies comprehension.
// *May 1 2013: 
//  Removed "DispatchSynchronized(IQEvent)".  Only QHsmWithQueue.cs should be used for async calls.
//  Made "public Dispatch(IQEvent)" into "protected CoreDispatch(IQEvent)".  A new "public Dispatch(IQEvent)" is virtual.
// *May 3 2013: 
//  Added debugger directive "DebuggerNonUserCode".  No point stepping through boiler-plate.
//  Added 'TraceEvent' delegate, for debugging, logging, or tracing.
// *May 9 2013: 
//  Added "MachineState", which supports persistence through online / offline status of state-machine.
//  Added dispatching of objects, instead of enums

using System;
using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace qf4net
{
    /// <summary>
    /// Basic QHsm capabilities, with no durable/persistent "extended state" capabilities.
    /// extended state.
    /// </summary>
    public abstract class QHsm : QHsm<string>
    {
        public QHsm(Type userSignals = null) : base(userSignals) { }

        public override string CurrentState
        {
            get { return m_MyStateMethod.Name; }
            protected set
            {
                if (machineState != MachineState.Offline)
                {
                    throw new InvalidOperationException("Cannot set QHSM state after Start().");
                }
                if (value != null)
                {
                    m_MyStateMethod = ImportState(value);
                }
            }
        }
    }

    /// <summary>
    /// Basic QHsm capabilities, with durable/persistent "extended state" captured ina memento object.
    /// </summary>
    /// <typeparam name="T">The memento type through which state is persisted and restored.</typeparam>
    public abstract class ExtendedQHsm<T> : QHsm<T> where T : class, IQHsmState, new()
    {
        public ExtendedQHsm(Type userSignals = null) : base(userSignals) { }

        public override T CurrentState
        {
            get
            {
                combinedState.Workflow = m_MyStateMethod.Name;
                return combinedState;
            }
            protected set
            {
                if (machineState != MachineState.Offline)
                {
                    throw new InvalidOperationException("Cannot set QHSM state after Start().");
                }
                if (value != null)
                {
                    //Allowing the workflow to be empty means that a 
                    //polymorphic initialization is possible.
                    if (!String.IsNullOrWhiteSpace(value.Workflow))
                    {
                        m_MyStateMethod = ImportState(value.Workflow);
                    }
                }else
                {
                    combinedState = new T();
                }
                combinedState = combinedState ?? value;
            }
        }
    }

    /// <summary>
    /// The base class for all hierarchical state machines
    /// </summary>
    [DebuggerNonUserCode]
    public abstract class QHsm<T> : IQHsm where T : class
    {
        private static QState s_TopState;

        protected T combinedState;

        /// <summary>
        /// Added for symmetry reasons, so that all deriving classes can add their own static 
        /// <see cref="TransitionChainStore"/> variable using the new key word.
        /// </summary>
        protected static TransitionChainStore s_TransitionChainStore = null;

        protected MachineState machineState = MachineState.Offline;
        protected MethodInfo m_MyStateMethod = s_TopState.GetMethodInfo();
        private MethodInfo m_MySourceStateMethod;
        protected TaskCompletionSource<T> completion;
        protected int AsyncRefCount;

        /// <summary>
        /// All asynchronous "action" invocations must be registered here, so that the state-machine
        /// can automatically call OnStop() when quiescence is achieved.  An auto-stop occurs either 
        /// when (1) any async routine has an unhandled exception, or when (2) all async routines are finished
        /// *and* there is no other event/message dispatching or waiting to be dispatched.
        /// </summary>
        protected Task Shunt
        {
            set
            {
                Interlocked.Increment(ref AsyncRefCount);
                value.ContinueWith(TryStop);
            }
        }

        protected void TryStop(Task t)
        {
            if (t.Status != TaskStatus.RanToCompletion)
            {
                //Because the failure path does not decrement the asyncRefCount,
                //it implicitly gives priority to exceptions...which is just
                //what we want.
                machineState = MachineState.Halted;
                if (t.Status == TaskStatus.Faulted)
                {
                    completion.TrySetException(t.Exception.GetBaseException());
                }
                else
                {
                    completion.TrySetCanceled();
                }
                Failed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                int count = Interlocked.Decrement(ref AsyncRefCount);
                if (count == 0)
                {
                    QuiesceNotify();
                }
            }
        }

        protected virtual void QuiesceNotify()
        {
            OnStop(EventArgs.Empty);
        }

        /// <summary>
        /// Assign for debugging or event recording
        /// </summary>
        public Action<MethodInfo, IQEvent> TraceEvent;
        protected Dictionary<string, int> mapper;
        private Type userSignalType;

        public virtual event EventHandler<EventArgs> Started = (sender, args) => { };
        /// <summary>
        /// Raised whenever the state machine has no pending work, whether in-queue or
        /// outstanding in the form of async-callbacks.
        /// </summary>
        public virtual event EventHandler<EventArgs> Quiescent = (sender, args) => { };
        /// <summary>
        /// Activated when a Task from an async action, assigned to the FailureTrap property, has 
        /// caused the state machine to terminate.
        /// </summary>
        public virtual event EventHandler<EventArgs> Failed = (sender, args) => { };

        static QHsm()
        {
            s_TopState = new QState(Top);
        }

        /// <summary>
        /// Can be used for typed message to signal conversion
        /// </summary>
        /// <param name="userSignals">a typeof(myEnum) value</param>
        public QHsm(Type userSignals = null)
        {
            userSignalType = userSignals;
            if (userSignals != null)
            {
                SetupMessageMap(userSignals);
            }
        }

        protected void SetupMessageMap(Type userSigs)
        {
            mapper = new Dictionary<string, int>();

            //BindingFlags.GetField had been one of the flags...
            foreach (FieldInfo field in userSigs.GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                object value = field.GetValue(null);
                int theVal = (int)Convert.ChangeType(value, userSigs);

                string name = null;

                object attrib = field.GetCustomAttributes(true).FirstOrDefault(attr => attr is MessageAttribute);

                //Adding user custom signals to the "key" is not necessary (theVal.TosString()), but
                //is done for completeness and debugging purposes.
                name = attrib == null ? theVal.ToString() : ((MessageAttribute)attrib).Type.Name;
                mapper.Add(name, theVal);
            }
        }

        protected virtual void OnStart(EventArgs arg)
        {
            Started(this, arg);
        }

        protected virtual void OnStop(EventArgs arg)
        {
            Quiescent(this, arg);
            if (completion != null)
            {
                completion.TrySetResult(CurrentState);
                completion = null;
            }
        }

        /// <summary>
        /// Getter for an optional <see cref="TransitionChainStore"/> that can hold cached
        /// <see cref="TransitionChain"/> objects that are used to optimize static transitions.
        /// </summary>
        protected virtual TransitionChainStore TransChainStore
        {
            get { return null; }
        }

        /// <summary>
        /// Is called inside of the function Init to give the deriving class a chance to
        /// initialize the state machine.
        /// </summary>
        protected abstract void InitializeStateMachine();

        /// <summary>
        /// Must only be called once by the client of the state machine to initialize the machine.
        /// </summary>
        private void Init()
        {
            //The Init() method originally was public, but it is more useful
            //to have "InitializeStateMachine()" called by Start/Stop entry points.
            Debug.Assert(m_MyStateMethod == s_TopState.GetMethodInfo()); // HSM not executed yet
            m_MySourceStateMethod = m_MyStateMethod;
            InitializeStateMachine();

            //All the code below is per the original Samek design.
            //It is being left here, as a fast reference.

            //MethodInfo stateMethod = m_MyStateMethod; // save m_StateHandler in a temporary

            //this.InitializeStateMachine(); // We call into the deriving class
            //// initial transition must go *one* level deep
            //Debug.Assert(GetSuperStateMethod(m_MyStateMethod) == stateMethod);

            //stateMethod = m_MyStateMethod; // Note: We only use the temporary
            //// variable stateMethod so that we can use Assert statements to ensure
            //// that each transition is only one level deep.
            //Trigger(stateMethod, QSignals.Entry);
            //while (Trigger(stateMethod, QSignals.Init) == null) // init handled?
            //{
            //    Debug.Assert(GetSuperStateMethod(m_MyStateMethod) == stateMethod);
            //    stateMethod = m_MyStateMethod;

            //    Trigger(stateMethod, QSignals.Entry);
            //}
        }

        /// <summary>
        /// Determines whether the state machine is in the state specified by <see paramref="inquiredState"/>.
        /// </summary>
        /// <param name="inquiredState">The state to check for.</param>
        /// <returns>
        /// <see langword="true"/> if the state machine is in the specified state; 
        /// <see langword="false"/> otherwise.
        /// </returns>
        /// <remarks>
        /// If the currently active state of a hierarchical state machine is s then it is in the 
        /// state s AND all its parent states.
        /// </remarks>
        public bool IsInState(QState inquiredState)
        {
            MethodInfo stateMethod;
            for (stateMethod = m_MyStateMethod;
                stateMethod != null;
                stateMethod = GetSuperStateMethod(stateMethod))
            {
                if (stateMethod == inquiredState.GetMethodInfo()) // do the states match?
                {
                    return true;
                }
            }
            return false; // no match found
        }

        /// <summary>
        /// Access to state needed for persisting and restoring the state-machine.
        /// </summary>
        public abstract T CurrentState { get; protected set; }

        /// <summary>
        /// Returns the name of the (deepest) state that the state machine is currently in.
        /// </summary>
        public string CurrentStateName
        {
            get { return m_MyStateMethod.Name; }
        }

        protected MethodInfo ImportState(string state)
        {
            var machineType = GetType();
            var method = machineType.GetMethod(state, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new InvalidOperationException(
                    $"'{state}' is not a valid state for {machineType.Name}."
                    );
            }
            return method;
        }

        private static Dictionary<MachineState, string> errMap = new Dictionary<MachineState, string>
        {
            [MachineState.Online] = "Cannot start an already started state-machine.",
            [MachineState.Halted] = "Cannot restart after fault.",
            [MachineState.Exiting] = "Cannot restart the state-machine until it is completely stopped."
        };

        public virtual void Start(T snapshot = null)
        {
            if (machineState != MachineState.Offline)
            {
                throw new InvalidOperationException(errMap[machineState]);
            }
            CurrentState = snapshot ?? combinedState;
            if (m_MyStateMethod == s_TopState.GetMethodInfo())  //CurrentStateName == "Top"
            {
                m_MySourceStateMethod = m_MyStateMethod;
                InitializeStateMachine();
            }else
            {
                OnEvent(m_MyStateMethod);
            }
            machineState = MachineState.Online;
        }

        public virtual void Stop()
        {
            machineState = MachineState.Offline;
            //Potentially, something more draconian could be done here,
            //such as...
            //completion.TrySetCanceled();
            OnStop(EventArgs.Empty);
        }

        /// <summary>
        /// Send a signal, with optional payload, into the state-machine.
        /// </summary>
        /// <remarks>
        /// This entry point is not thread safe.  Use the QhsmQ for multi-threaded dispatching.
        /// </remarks>
        /// <param name="qEvent">The user-defeined signal to dispatch.</param>
        /// <returns>A signal to indicate when the state-machine has finished processing events.</returns>
        public virtual Task Dispatch(IQEvent qEvent)
        {
            Task<T> token = null;

            if (completion != null)
            {
                token = Task.FromException<T>(new InvalidOperationException($"The QHsm base class is not re-entrant.  Use insteaad QHsmWithQueue for multi-threaded work."));
            }
            else if (machineState != MachineState.Online)
            {
                token = Task.FromException<T>(new InvalidOperationException($"Cannot dispatch when machine-state is {machineState.ToString()}"));
            }
            else 
            {
                completion = new TaskCompletionSource<T>();
                CoreDispatch(qEvent);
                token = completion.Task;
                if (Interlocked.Add(ref AsyncRefCount, 0) == 0){
                    OnStop(EventArgs.Empty);
                }
            }
            return token;
        }

        /// <summary>
        /// Reflection-based dispatch.
        /// </summary>
        /// <param name="message">Either a user signal enum value, or an object whose type is mapped to a user signal with a [MessageAttribute] annotation.</param>
        public Task Dispatch(object message)
        {
            Task retVal = null;
            if (message == null)
            {
                var name = GetType().Name;
                throw new InvalidOperationException($"QHSM '{name}': dispatching null messages is illegal.");
            }
            var msgType = message.GetType();

            int signal = 0;
            //Test if this object message is simply one of the custom user signals.
            if (msgType == userSignalType)
            {
                //In this case, there is no payload.
                signal = (int)message;
                retVal = Dispatch(new QEvent(signal));
            }
            else
            {
                string name = msgType.Name;
                if (mapper != null && mapper.TryGetValue(name, out signal))
                {
                    retVal = Dispatch(new QEvent(signal, message));
                }
            }

            retVal = retVal ?? Task.FromException<T>(new InvalidOperationException("A user signal object-to-enum mapping could not be found."));

            return retVal;
        }

        /// <summary>
        /// Dispatches the specified event to this state machine
        /// </summary>
        /// <param name="qEvent">The <see cref="IQEvent"/> to dispatch.</param>
        protected void CoreDispatch(IQEvent qEvent)
        {
            // We let the event bubble up the chain until it is handled by a state handler
            m_MySourceStateMethod = m_MyStateMethod;
            do
            {
                OnEvent(m_MySourceStateMethod, qEvent);
                QState state = (QState)m_MySourceStateMethod.Invoke(this, new object[] { qEvent });
                if (state != null)
                {
                    m_MySourceStateMethod = state.GetMethodInfo();
                }
                else
                {
                    m_MySourceStateMethod = null;
                }
            } while (m_MySourceStateMethod != null);
        }

        /// <summary>
        /// The handler for the top state that is shared by all instances of a QHSM.
        /// </summary>
        /// <param name="qEvent"></param>
        /// <returns></returns>
        private static QState Top(IQEvent qEvent)
        {
            return null;
        }

        /// <summary>
        /// The top state of each <see cref="QHsm"/>
        /// </summary>
        protected QState TopState
        {
            get { return s_TopState; }
        }

        #region Helper functions for the predefined signals

        private MethodInfo Trigger(MethodInfo stateMethod, QSignals qSignal)
        {
            var evt = new QEvent((int)qSignal);
            OnEvent(stateMethod, evt);
            QState state = (QState)stateMethod.Invoke(this, new object[] { evt });
            if (state == null)
            {
                return null;
            }
            else
            {
                return state.GetMethodInfo();
            }
        }

        private void OnEvent(MethodInfo info, IQEvent signal = null)
        {
            if (TraceEvent != null)
            {
                TraceEvent(info, signal);
            }
        }

        /// <summary>
        /// Sends the specified signal to the specified state and (optionally) records the transition
        /// </summary>
        /// <param name="receiverStateMethod">The <see cref="MethodInfo"/> that represents the state method
        /// to which to send the signal.</param>
        /// <param name="qSignal">The <see cref="QSignals"/> to send.</param>
        /// <param name="recorder">An instance of <see cref="TransitionChainRecorder"/> if the transition
        /// is to be recorded; <see langword="null"/> otherwise.</param>
        /// <returns>The <see cref="QState"/> returned by the state that recieved the signal.</returns>
        /// <remarks>
        /// Even if a recorder is specified, the transition will only be recorded if the state 
        /// <see paramref="receiverStateMethod"/> actually handled it.
        /// This function is used to record the transition chain for a static transition that is executed
        /// the first time. 
        /// </remarks>
        private MethodInfo Trigger(MethodInfo receiverStateMethod, QSignals qSignal, TransitionChainRecorder recorder)
        {
            MethodInfo stateMethod = Trigger(receiverStateMethod, qSignal);
            if ((stateMethod == null) && (recorder != null))
            {
                // The receiverState handled the event
                recorder.Record(receiverStateMethod, qSignal);
            }
            return stateMethod;
        }

        ///<summary>
        /// Retrieves the super state (parent state) of the specified 
        /// state by sending it the empty signal. 
        ///</summary>
        private MethodInfo GetSuperStateMethod(MethodInfo stateMethod)
        {
            QState superState = (QState)stateMethod.Invoke(this, new object[] { new QEvent((int)QSignals.Empty) });
            if (superState != null)
            {
                return superState.GetMethodInfo();
            }
            else
            {
                return null;
            }
        }


        #endregion

        /// <summary>
        /// Represents the macro Q_INIT in Miro Samek's implementation
        /// </summary>
        protected void InitializeState(QState state)
        {
            m_MyStateMethod = state.GetMethodInfo();
        }

        /// <summary>
        /// Performs a dynamic transition; i.e., the transition path is determined on the fly and not recorded.
        /// </summary>
        /// <param name="targetState">The <see cref="QState"/> to transition to.</param>
        protected void TransitionTo(QState targetState)
        {
            Debug.Assert(targetState != s_TopState); // can't target 'top' state

            OnEvent(targetState.GetMethodInfo());

            ExitUpToSourceState();
            // This is a dynamic transition. We pass in null instead of a recorder
            TransitionFromSourceToTarget(targetState.GetMethodInfo(), null);

            OnEvent(null);
        }

        /// <summary>
        /// Performs the transition from the current state to the specified target state.
        /// </summary>
        /// <param name="targetState">The <see cref="QState"/> to transition to.</param>
        /// <param name="transitionChain">A <see cref="TransitionChain"/> used to hold the transition chain that
        /// needs to be executed to perform the transition to the target state.</param>
        /// <remarks>
        /// The very first time that a given static transition is executed, the <see paramref="transitionChain"/> 
        /// reference will point to <see langword="null"/>. In this case a new <see cref="TransitionChain"/> 
        /// instance is created. As the complete transition is performed the individual transition steps are 
        /// recorded in the new <see cref="TransitionChain"/> instance. At the end of the call the new 
        /// (and now filled) <see cref="TransitionChain"/> is handed back to the caller.
        /// If the same transition needs to be performed later again, the caller needs to pass 
        /// in the filled <see cref="TransitionChain"/> instance. The recorded transition path will then be 
        /// played back very efficiently.
        /// </remarks>
        protected void TransitionTo(QState targetState, ref TransitionChain transitionChain)
        {
            Debug.Assert(targetState != s_TopState); // can't target 'top' state			
            ExitUpToSourceState();

            if (transitionChain == null) // for efficiency the first check is not thread-safe
            {
                // We implement the double-checked locking pattern
                TransitionToSynchronized(targetState, ref transitionChain);
            }
            else
            {
                // We just need to 'replay' the transition chain that is stored in the transitions chain.
                ExecuteTransitionChain(transitionChain);
            }
        }

        private void TransitionToSynchronized(QState targetState, ref TransitionChain transitionChain)
        {
            //The entire method had been embedded within [MethodImpl(MethodImplOptions.Synchronized)],
            //which is not available in .NET Core, and so a lock is used instead...
            lock (this)
            {
                if (transitionChain != null)
                {
                    // We encountered a race condition. The first (non-synchronized) check indicated that the transition chain
                    // is null. However, a second threat beat us in getting into this synchronized method and populated
                    // the transition chain in the meantime. We can execute the regular method again now.
                    TransitionTo(targetState, ref transitionChain);
                }
                else
                {
                    // The transition chain is not initialized yet, we need to dynamically retrieve
                    // the required transition steps and record them so that we can subsequently simply
                    // play them back.
                    TransitionChainRecorder recorder = new TransitionChainRecorder();
                    TransitionFromSourceToTarget(targetState.GetMethodInfo(), recorder);
                    // We pass the recorded transition steps back to the caller:
                    transitionChain = recorder.GetRecordedTransitionChain();
                }
            }
        }


        /// <summary>
        /// Performs a static transition from the current state to the specified target state. The
        /// <see cref="TransitionChain"/> that specifies the steps required for the static transition
        /// is specified by the provided index into the <see cref="TransitionChainStore"/>. Note that this
        /// method can only be used if the class that implements the <see cref="QHsm"/> provides a class 
        /// specific <see cref="TransitionChainStore"/> via the virtual getter <see cref="TransChainStore"/>.
        /// </summary>
        /// <param name="targetState">The <see cref="QState"/> to transition to.</param>
        /// <param name="chainIndex">The index into <see cref="TransitionChainStore"/> pointing to the 
        /// <see cref="TransitionChain"/> that is used to hold the individual transition steps that are 
        /// required to perform the transition.</param>
        /// <remarks>
        /// In order to use the method the calling class must retrieve the chain index during its static 
        /// construction phase by calling the method <see cref="TransitionChainStore.GetOpenSlot()"/> on
        /// its static <see cref="TransitionChainStore"/>.
        /// </remarks>
        protected void TransitionTo(QState targetState, int chainIndex)
        {
            // This method can only be used if a TransitionChainStore has been created for the QHsm
            Debug.Assert(this.TransChainStore != null);
            TransitionTo(targetState, ref this.TransChainStore.TransitionChains[chainIndex]);
        }

        private void ExitUpToSourceState()
        {
            for (MethodInfo stateMethod = m_MyStateMethod; stateMethod != m_MySourceStateMethod;)
            {
                Debug.Assert(stateMethod != null);
                MethodInfo stateMethodToHandleExit = Trigger(stateMethod, QSignals.Exit);
                if (stateMethodToHandleExit != null)
                {
                    // state did not handle the Exit signal itself
                    stateMethod = stateMethodToHandleExit;
                }
                else
                {
                    // state handled the Exit signal. We need to elicit
                    // the superstate explicitly.
                    stateMethod = GetSuperStateMethod(stateMethod);
                }
            }
        }

        /// <summary>
        /// Handles the transition from the source state to the target state without the help of a previously
        /// recorded transition chain.
        /// </summary>
        /// <param name="targetStateMethod">The <see cref="MethodInfo"/> representing the state method to transition to.</param>
        /// <param name="recorder">An instance of <see cref="TransitionChainRecorder"/> or <see langword="null"/></param>
        /// <remarks>
        /// Passing in <see langword="null"/> as the recorder means that we deal with a dynamic transition.
        /// If an actual instance of <see cref="TransitionChainRecorder"/> is passed in then we deal with a static
        /// transition that was not recorded yet. In this case the function will record the transition steps
        /// as they are determined.
        /// </remarks>
        private void TransitionFromSourceToTarget(MethodInfo targetStateMethod, TransitionChainRecorder recorder)
        {
            ArrayList statesTargetToLCA;
            int indexFirstStateToEnter;
            ExitUpToLCA(targetStateMethod, out statesTargetToLCA, out indexFirstStateToEnter, recorder);
            TransitionDownToTargetState(targetStateMethod, statesTargetToLCA, indexFirstStateToEnter, recorder);
        }

        /// <summary>
        /// Determines the transition chain between the target state and the LCA (Least Common Ancestor)
        /// and exits up to LCA while doing so.
        /// </summary>
        /// <param name="targetStateMethod">The target state method of the transition.</param>
        /// <param name="statesTargetToLCA">An <see cref="ArrayList"/> that holds (in reverse order) the states
        /// that need to be entered on the way down to the target state.
        /// Note: The index of the first state that needs to be entered is returned in 
        /// <see paramref="indexFirstStateToEnter"/>.</param>
        /// <param name="indexFirstStateToEnter">Returns the index in the array <see cparamref="statesTargetToLCA"/>
        /// that specifies the first state that needs to be entered on the way down to the target state.</param>
        /// <param name="recorder">An instance of <see cref="TransitionChainRecorder"/> if the transition chain
        /// should be recorded; <see langword="null"/> otherwise.</param>
        private void ExitUpToLCA(
            MethodInfo targetStateMethod,
            out ArrayList statesTargetToLCA,
            out int indexFirstStateToEnter,
            TransitionChainRecorder recorder)
        {
            statesTargetToLCA = new ArrayList();
            statesTargetToLCA.Add(targetStateMethod);
            indexFirstStateToEnter = 0;

            // (a) check my source state == target state (transition to self)
            if (m_MySourceStateMethod == targetStateMethod)
            {
                Trigger(m_MySourceStateMethod, QSignals.Exit, recorder);
                return;
            }

            // (b) check my source state == super state of the target state
            MethodInfo targetSuperStateMethod = GetSuperStateMethod(targetStateMethod);
            //Debug.WriteLine(targetSuperStateMethod.Name);
            if (m_MySourceStateMethod == targetSuperStateMethod)
            {
                return;
            }

            // (c) check super state of my source state == super state of target state
            // (most common)
            MethodInfo sourceSuperStateMethod = GetSuperStateMethod(m_MySourceStateMethod);
            if (sourceSuperStateMethod == targetSuperStateMethod)
            {
                Trigger(m_MySourceStateMethod, QSignals.Exit, recorder);
                return;
            }

            // (d) check super state of my source state == target
            if (sourceSuperStateMethod == targetStateMethod)
            {
                Trigger(m_MySourceStateMethod, QSignals.Exit, recorder);
                indexFirstStateToEnter = -1; // we don't enter the LCA
                return;
            }

            // (e) check rest of my source = super state of super state ... of target state hierarchy
            statesTargetToLCA.Add(targetSuperStateMethod);
            indexFirstStateToEnter++;
            for (MethodInfo stateMethod = GetSuperStateMethod(targetSuperStateMethod);
                stateMethod != null; stateMethod = GetSuperStateMethod(stateMethod))
            {
                if (m_MySourceStateMethod == stateMethod)
                {
                    return;
                }

                statesTargetToLCA.Add(stateMethod);
                indexFirstStateToEnter++;
            }

            // For both remaining cases we need to exit the source state
            Trigger(m_MySourceStateMethod, QSignals.Exit, recorder);

            // (f) check rest of super state of my source state ==
            //     super state of super state of ... target state
            // The array list is currently filled with all the states
            // from the target state up to the top state
            for (int stateIndex = indexFirstStateToEnter; stateIndex >= 0; stateIndex--)
            {
                if (sourceSuperStateMethod == (MethodInfo)statesTargetToLCA[stateIndex])
                {
                    indexFirstStateToEnter = stateIndex - 1;
                    // Note that we do not include the LCA state itself;
                    // i.e., we do not enter the LCA
                    return;
                }
            }

            // (g) check each super state of super state ... of my source state ==
            //     super state of super state of ... target state
            for (MethodInfo stateMethod = sourceSuperStateMethod;
                stateMethod != null; stateMethod = GetSuperStateMethod(stateMethod))
            {
                for (int stateIndex = indexFirstStateToEnter; stateIndex >= 0; stateIndex--)
                {
                    if (stateMethod == (MethodInfo)statesTargetToLCA[stateIndex])
                    {
                        indexFirstStateToEnter = stateIndex - 1;
                        // Note that we do not include the LCA state itself;
                        // i.e., we do not enter the LCA
                        return;
                    }
                }
                Trigger(stateMethod, QSignals.Exit, recorder);
            }

            // We should never get here
            throw new InvalidOperationException("Malformed Hierarchical State Machine");
        }

        private void TransitionDownToTargetState(
            MethodInfo targetStateMethod,
            ArrayList statesTargetToLCA,
            int indexFirstStateToEnter,
            TransitionChainRecorder recorder)
        {
            // we enter the states in the passed in array in reverse order
            for (int stateIndex = indexFirstStateToEnter; stateIndex >= 0; stateIndex--)
            {
                if (stateIndex == 0)
                {
                    //Acknowledging the target state, just a moment earlier than Samek's
                    //origina design, allows persistence models to function on the entry
                    //event of a new state.
                    m_MyStateMethod = targetStateMethod;
                }
                Trigger((MethodInfo)statesTargetToLCA[stateIndex], QSignals.Entry, recorder);
            }

            //Samek originally re-assigned the current state here:
            //m_MyStateMethod = targetStateMethod;

            // At last we are ready to initialize the target state.
            // If the specified target state handles init then the effective
            // target state is deeper than the target state specified in
            // the transition.
            while (Trigger(targetStateMethod, QSignals.Init, recorder) == null)
            {
                // Initial transition must be one level deep
                Debug.Assert(targetStateMethod == GetSuperStateMethod(m_MyStateMethod));
                targetStateMethod = m_MyStateMethod;
                Trigger(targetStateMethod, QSignals.Entry, recorder);
            }

            if (recorder != null)
            {
                // We always make sure that the last entry in the recorder represents the entry to the target state.
                EnsureLastTransistionStepIsEntryIntoTargetState(targetStateMethod, recorder);
                Debug.Assert(recorder.GetRecordedTransitionChain().Length > 0);
            }
        }

        private void EnsureLastTransistionStepIsEntryIntoTargetState(
            MethodInfo targetStateMethod,
            TransitionChainRecorder recorder)
        {
            if (recorder.GetRecordedTransitionChain().Length == 0)
            {
                // Nothing recorded so far
                RecordEntryIntoTargetState(targetStateMethod, recorder);
                return;
            }
            else
            {
                // We need to test whether the last recorded transition step is the entry into the target state
                TransitionChain transitionChain = recorder.GetRecordedTransitionChain();
                TransitionStep lastTransitionStep = transitionChain[transitionChain.Length - 1];
                if (lastTransitionStep.StateMethod != targetStateMethod ||
                    lastTransitionStep.QSignal != QSignals.Entry)
                {
                    RecordEntryIntoTargetState(targetStateMethod, recorder);
                    return;
                }
            }
        }

        private void RecordEntryIntoTargetState(
            MethodInfo targetStateMethod,
            TransitionChainRecorder recorder)
        {
            recorder.Record(targetStateMethod, QSignals.Entry);
        }

        private void ExecuteTransitionChain(TransitionChain transitionChain)
        {
            // There must always be at least one transition step in the provided transition chain
            Debug.Assert(transitionChain.Length > 0);

            TransitionStep transitionStep = transitionChain[0]; // to shut up the compiler; 
            // without it we would get the following error on the line 
            //       m_MyStateMethod = transitionStep.StateMethod;
            // at the end of this method: Use of possibly unassigned field 'State'
            for (int i = 0; i < transitionChain.Length; i++)
            {
                transitionStep = transitionChain[i];
                Trigger(transitionStep.StateMethod, transitionStep.QSignal);
            }
            m_MyStateMethod = transitionStep.StateMethod;
        }

        #region Helper classes for the handling of static transitions

        #region TransitionChainRecorder

        /// <summary>
        /// This class is used to record the individual transition steps that are required to transition from
        /// a given state to a target state.
        /// </summary>
        private class TransitionChainRecorder
        {
            private ArrayList m_TransitionSteps = new ArrayList();

            internal void Record(MethodInfo stateMethod, QSignals qSignal)
            {
                m_TransitionSteps.Add(new TransitionStep(stateMethod, qSignal));
            }

            /// <summary>
            /// Returns the recorded transition steps in form of a <see cref="TransitionChain"/> instance.
            /// </summary>
            /// <returns></returns>
            internal TransitionChain GetRecordedTransitionChain()
            {
                // We turn the ArrayList into a strongly typed array
                return new TransitionChain(m_TransitionSteps);
            }
        }

        #endregion

        #region TransitionChain & TransitionStep

        /// <summary>
        /// Class that wraps the handling of recorded transition steps. 
        /// </summary>
        protected class TransitionChain
        {
            private MethodInfo[] m_StateMethodChain;
            //  holds the transitions that need to be performed from the LCA down to the target state

            private BitArray m_ActionBits;
            // holds the actions that need to be performed on each transition in two bits:
            // 0x1: Init; 0x2: Entry, 0x3: Exit

            internal TransitionChain(ArrayList transitionSteps)
            {
                m_StateMethodChain = new MethodInfo[transitionSteps.Count];
                m_ActionBits = new BitArray(transitionSteps.Count * 2);

                for (int i = 0; i < transitionSteps.Count; i++)
                {
                    TransitionStep transitionStep = (TransitionStep)transitionSteps[i];

                    m_StateMethodChain[i] = transitionStep.StateMethod;
                    int bitPos = i * 2;
                    switch (transitionStep.QSignal)
                    {
                        case QSignals.Empty: m_ActionBits[bitPos] = false; m_ActionBits[++bitPos] = false; break;
                        case QSignals.Init: m_ActionBits[bitPos] = false; m_ActionBits[++bitPos] = true; break;
                        case QSignals.Entry: m_ActionBits[bitPos] = true; m_ActionBits[++bitPos] = false; break;
                        case QSignals.Exit: m_ActionBits[bitPos] = true; m_ActionBits[++bitPos] = true; break;
                    }
                }
            }

            internal int Length { get { return m_StateMethodChain.Length; } }

            internal TransitionStep this[int index]
            {
                get
                {
                    TransitionStep transitionStep = new TransitionStep();
                    transitionStep.StateMethod = m_StateMethodChain[index];

                    int bitPos = index * 2;
                    if (m_ActionBits[bitPos])
                    {
                        if (m_ActionBits[bitPos + 1])
                        {
                            transitionStep.QSignal = QSignals.Exit;
                        }
                        else
                        {
                            transitionStep.QSignal = QSignals.Entry;
                        }
                    }
                    else
                    {
                        if (m_ActionBits[bitPos + 1])
                        {
                            transitionStep.QSignal = QSignals.Init;
                        }
                        else
                        {
                            transitionStep.QSignal = QSignals.Empty;
                        }
                    }
                    return transitionStep;
                }
            }
        }

        internal struct TransitionStep
        {
            internal MethodInfo StateMethod;
            internal QSignals QSignal;

            internal TransitionStep(MethodInfo stateMethod, QSignals qSignal)
            {
                StateMethod = stateMethod;
                QSignal = qSignal;
            }
        }

        #endregion

        #region TransitionChainStore

        /// <summary>
        /// Class that handles storage and access to the various <see cref="TransitionChain"/> instances 
        /// that are required for all the static transitions in use by a given hierarchical state machine.
        /// </summary>
        protected class TransitionChainStore
        {
            private const int c_DefaultCapacity = 16;

            private TransitionChain[] m_Items;
            private int m_Size;

            /// <summary>
            /// Constructs a <see cref="TransitionChainStore"/>. The internal array for holding 
            /// <see cref="TransitionChain"/> instances is configured to have room for the static
            /// transitions in the base class (if any).
            /// </summary>
            /// <param name="callingClass">The class that called called the constructor.</param>
            public TransitionChainStore(Type callingClass)
            {
                Debug.Assert(IsDerivedFromQHsm(callingClass));

                Type baseType = callingClass.GetTypeInfo().BaseType;
                int slotsRequiredByBaseQHsm = 0;

                while (baseType != typeof(QHsm))
                {
                    slotsRequiredByBaseQHsm += RetrieveStoreSizeOfBaseClass(baseType);
                    baseType = baseType.GetTypeInfo().BaseType;
                }

                InitializeStore(slotsRequiredByBaseQHsm);
            }

            private int RetrieveStoreSizeOfBaseClass(Type baseType)
            {
                //BindingFlags bindingFlags =
                //    BindingFlags.DeclaredOnly |
                //    BindingFlags.NonPublic |
                //    BindingFlags.Static |
                //    BindingFlags.GetField;
                //var oof = MemberTypes
                //MemberInfo[] mi = baseType.FindMembers(MemberTypes.Field, bindingFlags,
                //    Type.FilterName, "s_TransitionChainStore");

                //if (mi.Length < 1)
                //{
                //    return 0;
                //}

                //TransitionChainStore store = (TransitionChainStore)baseType.InvokeMember(
                //    "s_TransitionChainStore", bindingFlags, null, null, null);
                //return store.Size;
                throw new NotImplementedException("This QHSM method is not yet converted to .NET Core");
            }

            private bool IsDerivedFromQHsm(Type type)
            {
                Type baseType = type.GetTypeInfo().BaseType;
                while ((baseType != null))
                {
                    if (baseType == typeof(QHsm))
                    {
                        return true;
                    }
                    baseType = baseType.GetTypeInfo().BaseType;
                }

                // None of the base classes is QHsm
                return false;
            }

            private void InitializeStore(int slotsRequiredByBaseQHsm)
            {
                if (slotsRequiredByBaseQHsm == 0)
                {
                    m_Items = new TransitionChain[c_DefaultCapacity];
                }
                else
                {
                    m_Items = new TransitionChain[2 * slotsRequiredByBaseQHsm];
                }

                m_Size = slotsRequiredByBaseQHsm;
            }

            /// <summary>
            /// Creates a new slot for a <see cref="TransitionChain"/> and returns its index
            /// </summary>
            /// <returns>The index of the new slot.</returns>
            public int GetOpenSlot()
            {
                if (m_Size >= m_Items.Length)
                {
                    // We no longer have room in the items array to hold a new slot
                    IncreaseCapacity();
                }
                return m_Size++;
            }

            /// <summary>
            /// Reallocates the internal array <see cref="m_Items"/> to an array twice the previous capacity.
            /// </summary>
            private void IncreaseCapacity()
            {
                int newCapacity;
                if (m_Items.Length == 0)
                {
                    newCapacity = c_DefaultCapacity;
                }
                else
                {
                    newCapacity = m_Items.Length * 2;
                }
                TransitionChain[] newItems = new TransitionChain[newCapacity];
                Array.Copy(m_Items, 0, newItems, 0, m_Items.Length);
                m_Items = newItems;
            }

            /// <summary>
            /// Should be called once all required slots have been established in order to minimize the memory 
            /// footprint of the store.
            /// </summary>
            public void ShrinkToActualSize()
            {
                TransitionChain[] newItems = new TransitionChain[m_Size];
                Array.Copy(m_Items, 0, newItems, 0, m_Size);
                m_Items = newItems;
            }

            /// <summary>
            /// Provides access to the array that holds the persisted <see cref="TransitionChain"/> objects.
            /// </summary>
            public TransitionChain[] TransitionChains
            {
                get { return m_Items; }
            }

            /// <summary>
            /// The size of the <see cref="TransitionChainStore"/>; i.e., the actual number of used slots.
            /// </summary>
            internal int Size
            {
                get { return m_Size; }
            }
        }

        #endregion

        #endregion
    }
}
