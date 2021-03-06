#!/bin/bash

if [ "x$BENCH_SIZE" = x ] ; then
    BENCH_SIZE=yes
fi
if [ "x$BENCH_WALL_CLOCK" = x ] ; then
    BENCH_WALL_CLOCK=yes
fi
if [ "x$BENCH_PAUSE_TIME" = x ] ; then
    BENCH_PAUSE_TIME=no
fi

if [ "x$1" = "x" ] ; then
    echo "Usage: speedtest.sh <conf-file>"
    exit 1
fi

if [ ! -f "$1" ] ; then
    echo "Error: Config file '$1' does not exist."
    exit 1
fi

DIR=`pwd`

mono_env () {
    true
}

benchmark_env () {
    true
}

. "$1"

mono_env

TIME="$DIR/mytime/mytime"
OUTDIR="$DIR/results"
TMPPREFIX="/tmp/speedtest$$"
TIMEOUT=200

grepscimark () {
    grep Composite "$TMPPREFIX.out" | awk '{ print $3 }' >"$OUTDIR/scimark.times"
    grep FFT "$TMPPREFIX.out" | awk '{ print $3 }' >"$OUTDIR/scimark-fft.times"
    grep SOR "$TMPPREFIX.out" | awk '{ print $3 }' >"$OUTDIR/scimark-sor.times"
    grep Monte "$TMPPREFIX.out" | awk '{ print $4 }' >"$OUTDIR/scimark-montecarlo.times"
    grep Sparse "$TMPPREFIX.out" | awk '{ print $4 }' >"$OUTDIR/scimark-matmult.times"
    grep LU "$TMPPREFIX.out" | awk '{ print $3 }' >"$OUTDIR/scimark-lu.times"
}

runtest () {
    echo "$1"

    benchmark_env "$1"

    pushd "tests/$2" >/dev/null

    measure="$3"

    if [ "$BENCH_SIZE" = yes ] ; then
	#the size run is not timed
	$TIME /dev/null "$TIMEOUT" "$MONO" --stats $4 $5 $6 $7 $8 $9 >"$TMPPREFIX.stats" 2>/dev/null
	if [ $? -ne 0 ] ; then
	    echo "Error"
	    popd >/dev/null
	    return
	fi
	grep -a '^Native code size' "$TMPPREFIX.stats" | awk '{ print $5 }' >"$OUTDIR/$1.size"

	echo "Size"
	cat "$OUTDIR/$1.size"
    fi

    if [ "$BENCH_WALL_CLOCK" = yes ] ; then
	rm -f "$TMPPREFIX.times" "$TMPPREFIX.out"
	i=1
	while [ $i -le $COUNT ] ; do
	    if [ "$measure" = time ] ; then
		$TIME "$TMPPREFIX.times" "$TIMEOUT" "$MONO" $4 $5 $6 $7 $8 $9 >/dev/null 2>&1
	    else
		$TIME /dev/null "$TIMEOUT" "$MONO" $4 $5 $6 $7 $8 $9 >>"$TMPPREFIX.out"
	    fi
	    if [ $? -ne 0 ] ; then
		echo "Error"
		popd >/dev/null
		return
	    fi
	    i=$(($i + 1))
	done

	if [ "$measure" = time ] ; then
	    cp "$TMPPREFIX.times" "$OUTDIR/$1.times"
	    rm "$TMPPREFIX.times"
	else
	    $measure
	fi

	echo "Times"
	cat "$OUTDIR/$1.times"
    fi

    if [ "$BENCH_PAUSE_TIME" = yes ] ; then
	if [ "x$PAUSE_COUNT" = "x" ] ; then
	    PAUSE_COUNT=3
	fi

	rm -f "$TMPPREFIX.pauses"
	i=1
	while [ $i -le $PAUSE_COUNT ] ; do
	    echo "*** run $i" >>"$TMPPREFIX.pauses"
	    if sudo dtrace -l -P 'mono$target' -c "$MONO" | grep gc-concurrent-update-finish-begin >/dev/null ; then
		SCRIPT='mono$target:::gc-world-stop-begin { self->ts = timestamp; self->concurrent = 0; } mono$target:::gc-concurrent-start-begin { self->concurrent = 1; } mono$target:::gc-concurrent-update-finish-begin { self->concurrent = 1; } mono$target:::gc-world-restart-end { printf ("\npause-time %d %d %d\n", arg0, arg0 && self->concurrent, (timestamp - self->ts)/1000); }'
	    else
		SCRIPT='mono$target:::gc-world-stop-begin { self->ts = timestamp; self->concurrent = 0; } mono$target:::gc-world-restart-end { printf ("\npause-time %d %d %d\n", 1, 0, (timestamp - self->ts)/1000); }'
	    fi
	    sudo MONO_GC_PARAMS="$MONO_GC_PARAMS" dtrace -q -c "$MONO $4 $5 $6 $7 $8 $9" -n "$SCRIPT" >>"$TMPPREFIX.pauses"
	    if [ $? -ne 0 ] ; then
		echo "Error"
		popd >/dev/null
		return
	    fi
	    i=$(($i + 1))
	done

	cp "$TMPPREFIX.pauses" "$OUTDIR/$1.pauses"
	rm "$TMPPREFIX.pauses"
    fi

    popd >/dev/null
}

if [ ! -f "$TIME" ] ; then
    echo Building mytime
    pushd mytime >/dev/null
    make
    if [ $? -ne 0 ] ; then
	popd >/dev/null
	echo "Build failed"
	exit 1
    fi
    popd >/dev/null
fi

if [ ! -d "$OUTDIR" ] ; then
    mkdir "$OUTDIR"
fi

if [ ! -f "$MONO" ] ; then
    echo "Error: Missing mono.  Expected to be $MONO."
    exit 1
fi

#runtest myfib small time myfib.exe
#runtest monofib small time fib.exe 42
#runtest scimark scimark grepscimark scimark.exe
#runtest gmcs gmcs time gmcs.exe -define:NET_1_1 -out:mcs.exe @mcs.exe.sources cs-parser.cs

runtest euler csgrande/Euler/Euler/bin/Debug time Euler.exe
runtest grandetracer csgrande/GrandeTracer/GrandeTracer/bin/Debug time GrandeTracer.exe
runtest bh csolden/BH/BH/bin/Debug time BH.exe -b 700 -s 1000
runtest bisort csolden/BiSort/BiSort/bin/Debug time BiSort.exe -s 3000000
runtest health csolden/Health/Health/bin/Debug time Health.exe -l 10 -t 40
runtest perimeter csolden/Perimeter/Perimeter/bin/Debug time Perimeter.exe -l 17
runtest specraytracer csspec/SpecRaytracer/SpecRaytracer/bin/Debug time SpecRaytracer.exe 200 20000 ../time-test.model
runtest db csspec/DB/DB/bin/Debug time DB.exe ../input/db6 ../input/scr6
runtest ahcbench zorn/ahcbench time ahcbench.exe input3.cs
runtest lcscbench zorn/lcscbench time lcscbench.exe input3.cs
runtest sharpsatbench zorn/SharpSATbench time SharpSATbench.exe input3.cnf

runtest fsharp f-sharp-2.0 time fsc.exe GeneralTest1.fs
runtest ipy IronPython-2.0B2 time ipy.exe pystone.py 500000

runtest binarytree shootout time binarytree.exe 19
runtest except shootout time except.exe 10000000
runtest hash shootout time hash.exe 10000000
runtest lists shootout time lists.exe 30000
runtest mandelbrot shootout time mandelbrot.exe 6400
runtest message shootout time message.exe 1000000
runtest n-body shootout time n-body.exe 50000000
runtest objinst shootout time objinst.exe 1000000000
runtest raytracer2 shootout time raytracer.csharp-2.exe 250
runtest raytracer3 shootout time raytracer.csharp-3.exe 600
runtest strcat shootout time strcat.exe 80000000

runtest graph4 graph time graph4.exe
runtest graph8 graph time graph8.exe
#runtest compileswf compile time --compile-all System.Windows.Forms.dll
