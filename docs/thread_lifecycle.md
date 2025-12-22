# Thread Lifecycle Overview

This program uses **four distinct categories of threads**, each with a specific role and lifecycle.
They are designed to start in a controlled order, run independently, and shut down cleanly.

---

## 1. Main (Host) Thread

### Role

* Acts as the **orchestrator** of the entire application.
* Initializes all components.
* Controls startup and shutdown.
* Does not perform heavy processing.

### Lifecycle

1. **Startup**

    * Parses command-line arguments.
    * Validates configuration (paths, limits, worker counts).
    * Constructs all core components.

2. **System Start**

    * Starts worker threads.
    * Starts the reporting thread.
    * Starts filesystem monitoring.

3. **Idle / Supervisory Phase**

    * Waits for shutdown signals (e.g., Ctrl+C).
    * Does not process logs or events directly.

4. **Shutdown**

    * Receives termination signal.
    * Initiates shutdown in a defined order.
    * Waits for all background threads to finish.
    * Exits the process cleanly.

### Termination

* Exits only after all other threads have been stopped and joined.

---

## 2. Filesystem Watcher Thread(s)

### Role

* Receives notifications from the operating system about file changes.
* Converts those notifications into internal events.
* Publishes events to the internal queue.

### Lifecycle

1. **Startup**

    * Created when filesystem monitoring is enabled.
    * Begins listening for file change notifications.

2. **Event Handling**

    * Executes lightweight callbacks when files change.
    * Each callback:

        * Identifies the type of change.
        * Publishes an event to the queue.
    * Callbacks are intentionally short and non-blocking.

3. **Steady State**

    * Runs continuously while monitoring is enabled.
    * May produce events faster than they are consumed.

4. **Shutdown**

    * Monitoring is disabled by the host thread.
    * No new events are published.

### Termination

* Managed by the runtime.
* Ends once monitoring is stopped and the watcher is disposed.

### Notes

* These threads are not under direct control of the application.
* They must never block or perform heavy work.

---

## 3. Worker Threads (Processing Threads)

### Role

* Perform the core work of the system.
* Consume events from the queue.
* Read and process log files.
* Update per-interval statistics.

### Lifecycle

Each worker thread follows the same lifecycle.

#### 1. Startup

* Created and started by the coordinator.
* Assigned:

    * A shared event queue.
    * Its own statistics buffers.

#### 2. Processing Loop

Repeated until shutdown:

1. Waits for an event from the queue.
2. Routes the event based on its type (create, modify, delete, rename).
3. Acquires exclusive access to the affected file (if applicable).
4. Reads any newly appended log data.
5. Parses log lines and updates statistics.
6. Releases file access.
7. Acknowledges any pending statistics swap requests.

Only one worker processes a given file at a time, but multiple workers may process different files concurrently.

#### 3. Idle State

* If no events are available, the worker waits efficiently.
* Does not consume CPU unnecessarily.

#### 4. Shutdown

* Receives a stop signal via the event queue being closed.
* Exits its loop after finishing the current event.
* Performs no further processing.

### Termination

* Explicitly joined by the coordinator.
* Guaranteed to exit cleanly without abrupt interruption.

---

## 4. Reporter Thread (Statistics Aggregation Thread)

### Role

* Periodically collects statistics from all workers.
* Produces human-readable reports.
* Measures system health and performance.

### Lifecycle

#### 1. Startup

* Created by the host after workers are running.
* Initializes timers and measurement baselines.

#### 2. Reporting Loop

Repeated until shutdown:

1. Waits approximately the configured reporting interval.
2. Requests workers to swap their statistics buffers.
3. Waits for all workers to acknowledge the swap.
4. Merges the collected statistics.
5. Computes derived metrics (rates, percentiles, top messages).
6. Prints a report to the console.

The reporter does **not** interfere with ongoing processing; workers continue using fresh buffers.

#### 3. Idle State

* Sleeps between reporting intervals.
* Wakes only to generate reports.

#### 4. Shutdown

* Receives stop signal from the host.
* Optionally performs one final report.
* Exits its loop.

### Termination

* Joined by the host thread.
* Ensures no partial or inconsistent reports are printed.

---

## 5. Garbage Collection (Runtime-Managed Threads)

### Role

* Managed entirely by the .NET runtime.
* Reclaim unused memory.
* Ensure memory safety.

### Lifecycle

* Created and managed automatically.
* Run intermittently based on memory pressure.
* May temporarily pause other threads.

### Impact on the Program

* May delay processing briefly.
* Never corrupts program state.
* Reporting accounts for pauses by using actual elapsed time.

---

## 6. Thread Interaction Summary

| Thread Type        | Starts          | Runs               | Stops               |
|--------------------|-----------------|--------------------|---------------------|
| Main Thread        | Process start   | Supervises         | Last                |
| Filesystem Watcher | After startup   | While enabled      | Before workers      |
| Worker Threads     | Early startup   | Until queue closed | Before reporter     |
| Reporter Thread    | After workers   | Periodic           | Before process exit |
| GC Threads         | Runtime-managed | As needed          | Runtime-managed     |

---

## 7. Shutdown Order (Why It Matters)

Correct shutdown order ensures:

1. No new events are produced.
2. All queued events are handled or discarded safely.
3. Workers finish cleanly.
4. Final statistics are consistent.
5. Process exits without resource leaks.

Order:

1. Stop filesystem watcher
2. Stop event queue
3. Stop worker threads
4. Stop reporter
5. Exit main thread

---

## Summary

Each thread in this program:

* Has a **single, well-defined responsibility**
* Starts and stops in a predictable way
* Communicates only through controlled synchronization points

This design ensures the system remains **stable, observable, and correct**, even under heavy load or unexpected
filesystem behavior.

