package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * Guards the shape of the API Swift sees.
 *
 * Kotlin/Native only converts a thrown exception into an NSError when the function declares what it throws.
 * A public suspend function without `@Throws` terminates the whole process instead — so on iOS a wrong
 * password or a dropped connection kills the app, and Swift's `do`/`catch` never runs. Nothing on the JVM
 * notices, which is exactly why this has to be asserted rather than remembered.
 *
 * `@Throws` compiles to a real `throws` clause in bytecode, so Java reflection can see it without pulling in
 * kotlin-reflect.
 */
class ExportedApiTest {

    @Test
    fun `every exported suspend function declares what it throws`() {
        val missing = listOf(RemoteSession::class.java, DeskPair::class.java)
            .flatMap { type -> type.declaredMethods.map { type to it } }
            .filter { (_, method) ->
                java.lang.reflect.Modifier.isPublic(method.modifiers) &&
                    // The compiler emits a synthetic $default bridge for every default argument; Swift
                    // never sees those, and they carry no throws clause of their own.
                    !method.isSynthetic &&
                    method.isSuspend()
            }
            .filter { (_, method) -> method.exceptionTypes.isEmpty() }
            .map { (type, method) -> "${type.simpleName}.${method.name}" }

        if (missing.isNotEmpty()) {
            fail(
                "These are callable from Swift and would terminate the app rather than throw: " +
                    "${missing.sorted()}. Annotate each with " +
                    "@Throws(CancellationException::class, Throwable::class).",
            )
        }
    }

    @Test
    fun `the test can tell an annotated function from a bare one`() {
        // Without this the test above passes just as happily when isSuspend stops matching anything.
        val annotated = RemoteSession::class.java.declaredMethods.count {
            it.isSuspend() && it.exceptionTypes.isNotEmpty()
        }
        assertTrue(annotated >= 6, "expected the session's send methods to be found, saw $annotated")
    }

    /** A suspend function's last parameter is the continuation the compiler adds. */
    private fun java.lang.reflect.Method.isSuspend(): Boolean =
        parameterTypes.lastOrNull() == kotlin.coroutines.Continuation::class.java
}
