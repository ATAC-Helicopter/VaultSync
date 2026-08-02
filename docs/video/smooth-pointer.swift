#!/usr/bin/env swift

import ApplicationServices
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(2)
}

func number(_ value: String, name: String) -> Double {
    guard let result = Double(value) else {
        fail("\(name) must be a number")
    }
    return result
}

func ease(_ progress: Double) -> Double {
    if progress < 0.5 {
        return 4 * progress * progress * progress
    }
    let value = -2 * progress + 2
    return 1 - value * value * value / 2
}

func move(to target: CGPoint, duration: Double) {
    let source = CGEventSource(stateID: .hidSystemState)
    let start = CGEvent(source: nil)?.location ?? target
    let distance = hypot(target.x - start.x, target.y - start.y)
    let samples = max(24, min(120, Int(distance / 8)))

    for index in 1...samples {
        let progress = ease(Double(index) / Double(samples))
        let point = CGPoint(
            x: start.x + (target.x - start.x) * progress,
            y: start.y + (target.y - start.y) * progress
        )
        CGEvent(
            mouseEventSource: source,
            mouseType: .mouseMoved,
            mouseCursorPosition: point,
            mouseButton: .left
        )?.post(tap: .cghidEventTap)
        Thread.sleep(forTimeInterval: duration / Double(samples))
    }
}

func click(at point: CGPoint) {
    let source = CGEventSource(stateID: .hidSystemState)
    CGEvent(
        mouseEventSource: source,
        mouseType: .leftMouseDown,
        mouseCursorPosition: point,
        mouseButton: .left
    )?.post(tap: .cghidEventTap)
    Thread.sleep(forTimeInterval: 0.11)
    CGEvent(
        mouseEventSource: source,
        mouseType: .leftMouseUp,
        mouseCursorPosition: point,
        mouseButton: .left
    )?.post(tap: .cghidEventTap)
}

func drag(from start: CGPoint, to target: CGPoint, duration: Double) {
    move(to: start, duration: min(0.8, duration * 0.25))
    let source = CGEventSource(stateID: .hidSystemState)
    CGEvent(
        mouseEventSource: source,
        mouseType: .leftMouseDown,
        mouseCursorPosition: start,
        mouseButton: .left
    )?.post(tap: .cghidEventTap)
    Thread.sleep(forTimeInterval: 0.14)

    let distance = hypot(target.x - start.x, target.y - start.y)
    let samples = max(24, min(120, Int(distance / 6)))
    for index in 1...samples {
        let progress = ease(Double(index) / Double(samples))
        let point = CGPoint(
            x: start.x + (target.x - start.x) * progress,
            y: start.y + (target.y - start.y) * progress
        )
        CGEvent(
            mouseEventSource: source,
            mouseType: .leftMouseDragged,
            mouseCursorPosition: point,
            mouseButton: .left
        )?.post(tap: .cghidEventTap)
        Thread.sleep(forTimeInterval: duration / Double(samples))
    }

    CGEvent(
        mouseEventSource: source,
        mouseType: .leftMouseUp,
        mouseCursorPosition: target,
        mouseButton: .left
    )?.post(tap: .cghidEventTap)
}

func scroll(pixels: Int32, duration: Double) {
    let steps = max(18, min(90, Int(abs(pixels) / 10)))
    var emitted: Int32 = 0

    for index in 1...steps {
        let target = Int32((Double(pixels) * ease(Double(index) / Double(steps))).rounded())
        let delta = target - emitted
        emitted = target
        if delta != 0 {
            CGEvent(
                scrollWheelEvent2Source: nil,
                units: .pixel,
                wheelCount: 1,
                wheel1: delta,
                wheel2: 0,
                wheel3: 0
            )?.post(tap: .cghidEventTap)
        }
        Thread.sleep(forTimeInterval: duration / Double(steps))
    }
}

let arguments = CommandLine.arguments
guard arguments.count >= 2 else {
    fail("Usage: smooth-pointer.swift move|click|scroll ...")
}

switch arguments[1] {
case "move":
    guard arguments.count == 5 else {
        fail("Usage: smooth-pointer.swift move X Y SECONDS")
    }
    move(
        to: CGPoint(
            x: number(arguments[2], name: "X"),
            y: number(arguments[3], name: "Y")
        ),
        duration: number(arguments[4], name: "SECONDS")
    )
case "click":
    guard arguments.count == 5 else {
        fail("Usage: smooth-pointer.swift click X Y SECONDS")
    }
    let target = CGPoint(
        x: number(arguments[2], name: "X"),
        y: number(arguments[3], name: "Y")
    )
    move(to: target, duration: number(arguments[4], name: "SECONDS"))
    Thread.sleep(forTimeInterval: 0.22)
    click(at: target)
case "scroll":
    guard arguments.count == 4, let pixels = Int32(arguments[2]) else {
        fail("Usage: smooth-pointer.swift scroll PIXELS SECONDS")
    }
    scroll(pixels: pixels, duration: number(arguments[3], name: "SECONDS"))
case "drag":
    guard arguments.count == 7 else {
        fail("Usage: smooth-pointer.swift drag FROM_X FROM_Y TO_X TO_Y SECONDS")
    }
    drag(
        from: CGPoint(
            x: number(arguments[2], name: "FROM_X"),
            y: number(arguments[3], name: "FROM_Y")
        ),
        to: CGPoint(
            x: number(arguments[4], name: "TO_X"),
            y: number(arguments[5], name: "TO_Y")
        ),
        duration: number(arguments[6], name: "SECONDS")
    )
default:
    fail("Unknown command: \(arguments[1])")
}
