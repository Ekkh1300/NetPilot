package com.netpilot.mobile.core

import android.content.Context
import com.netpilot.mobile.data.DnsCatalog
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.data.Store
import com.netpilot.mobile.net.DnsBenchmark
import com.netpilot.mobile.net.Monitor

/**
 * Process-wide singletons. [init] is called once from [com.netpilot.mobile.App].
 *
 * Order matters: the store must exist before the catalog/repos read from it, and the
 * language must be applied before the first frame so screens never render in the
 * wrong language and then flip.
 */
object AppGraph {

    @Volatile
    private var ready = false

    lateinit var store: Store
        private set
    lateinit var dnsCatalog: DnsCatalog
        private set
    lateinit var benchmark: DnsBenchmark
        private set

    fun init(context: Context) {
        if (ready) return
        synchronized(this) {
            if (ready) return
            val app = context.applicationContext

            store = Store(app)
            dnsCatalog = DnsCatalog(store)
            benchmark = DnsBenchmark(store)

            // Language first, telemetry second (both are idempotent).
            Repo.loadLanguage()
            Monitor.start(app, Repo.monitorInterval())

            ready = true
        }
    }
}
