pluginManagement {
    repositories {
        // The upstream repositories come first, and the mirror is only a fallback.
        //
        // This was the other reason CI never went green. The list was ordered mirror-first with
        // no way to fall back: the Aliyun mirrors are reachable from this one machine (where
        // dl.google.com is blocked) and from nowhere else, so on a GitHub runner Gradle tried
        // them, they did not answer, and the build died before it reached google() or
        // mavenCentral() - which were present all along, further down the same list.
        //
        // Reordering is the whole fix. A machine that cannot reach dl.google.com will now try it,
        // fail, and fall through to the mirror; a machine that can will never touch the mirror at
        // all. Nothing needs to be configured per machine and nothing silently stops working.
        google()
        mavenCentral()
        gradlePluginPortal()

        // Fallback for networks that block the above. Deliberately last, and deliberately without
        // `|| true`-style tolerance: if a repository is declared, a failure to reach it should be
        // visible rather than swallowed.
        maven(url = "https://maven.aliyun.com/repository/google")
        maven(url = "https://maven.aliyun.com/repository/public")
        maven(url = "https://maven.aliyun.com/repository/gradle-plugin")
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        google()
        mavenCentral()

        // Same ordering rationale as pluginManagement: a fallback for a restricted network, not
        // the primary. Listed last so a machine that has Google Maven never pays for the mirror.
        maven(url = "https://maven.aliyun.com/repository/google")
        maven(url = "https://maven.aliyun.com/repository/public")
    }
}

rootProject.name = "NetPilotMobile"
include(":app")