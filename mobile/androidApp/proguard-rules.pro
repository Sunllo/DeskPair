# R8 is on for release. These rules cover the three places in this app where a class is reached by name
# rather than by a call R8 can see, which is exactly where shrinking goes wrong silently: the app builds,
# installs and then fails at the first connection.

# Wire's generated messages. Each one's adapter is a static ADAPTER field that the runtime also looks up
# reflectively when decoding a nested message, so the field has to survive even where nothing calls it.
-keepclassmembers class * extends com.squareup.wire.Message {
    public static final com.squareup.wire.ProtoAdapter ADAPTER;
}
-keepclassmembers class * extends com.squareup.wire.Message$Builder { <fields>; }

# kotlinx.serialization generates a $serializer object per @Serializable class and finds it through the
# class's Companion. Both are referenced only by name.
-keepattributes *Annotation*, InnerClasses
-keepclassmembers @kotlinx.serialization.Serializable class ** {
    *** Companion;
    *** INSTANCE;
    kotlinx.serialization.KSerializer serializer(...);
}
-keepclasseswithmembers class **$$serializer { *; }

# kotlinx.serialization reads enum entries by name when a JSON value does not match ordinal order.
-keepclassmembers class * extends java.lang.Enum { <fields>; }

# The crypto providers register themselves through a ServiceLoader, which R8 cannot follow.
-keep class dev.whyoleg.cryptography.** { *; }

# That library's JDK provider carries an optional bridge to Bouncy Castle, used only to derive a public key
# from a private one. Bouncy Castle is not a dependency here and the bridge is never reached: DeviceIdentity
# stores both halves of the pair, precisely because deriving one from the other is not offered by this
# library's API. Without this, R8 refuses to finish over classes that will never be loaded.
-dontwarn org.bouncycastle.**

# Coroutines keeps its own rules in the artifact; this one is the exception R8 still warns about.
-dontwarn kotlinx.coroutines.**
